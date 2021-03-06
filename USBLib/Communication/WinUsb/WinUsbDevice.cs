﻿using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using UCIS.USBLib.Descriptor;
using UCIS.USBLib.Internal.Windows;

namespace UCIS.USBLib.Communication.WinUsb {
	class SafeWinUsbInterfaceHandle : SafeHandleZeroOrMinusOneIsInvalid {
		public SafeWinUsbInterfaceHandle() : base(true) { }
		protected override bool ReleaseHandle() {
			if (IsInvalid) return true;
			bool bSuccess = WinUsbDevice.WinUsb_Free(handle);
			handle = IntPtr.Zero;
			return bSuccess;
		}
	}
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	struct UsbSetupPacket {
		public byte RequestType;
		public byte Request;
		public short Value;
		public short Index;
		public short Length;
		public UsbSetupPacket(byte requestType, byte request, short value, short index, short length) {
			RequestType = requestType;
			Request = request;
			Value = value;
			Index = index;
			Length = length;
		}
	}
	[SuppressUnmanagedCodeSecurity]
	public class WinUsbDevice : UsbInterface, IUsbDevice {
		const string WIN_USB_DLL = "winusb.dll";
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, out SafeWinUsbInterfaceHandle InterfaceHandle);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		internal static extern bool WinUsb_GetAssociatedInterface(SafeWinUsbInterfaceHandle InterfaceHandle, byte AssociatedInterfaceIndex, out SafeWinUsbInterfaceHandle AssociatedInterfaceHandle);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		internal static extern bool WinUsb_Free(IntPtr InterfaceHandle);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_AbortPipe(SafeWinUsbInterfaceHandle InterfaceHandle, byte PipeID);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_ControlTransfer(SafeWinUsbInterfaceHandle InterfaceHandle, UsbSetupPacket SetupPacket, IntPtr Buffer, int BufferLength, out int LengthTransferred, IntPtr pOVERLAPPED);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_FlushPipe(SafeWinUsbInterfaceHandle InterfaceHandle, byte PipeID);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_GetDescriptor(SafeWinUsbInterfaceHandle InterfaceHandle, byte DescriptorType, byte Index, ushort LanguageID, IntPtr Buffer, int BufferLength, out int LengthTransferred);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_ReadPipe(SafeWinUsbInterfaceHandle InterfaceHandle, byte PipeID, Byte[] Buffer, int BufferLength, out int LengthTransferred, IntPtr pOVERLAPPED);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_ReadPipe(SafeWinUsbInterfaceHandle InterfaceHandle, byte PipeID, IntPtr pBuffer, int BufferLength, out int LengthTransferred, IntPtr pOVERLAPPED);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_ResetPipe(SafeWinUsbInterfaceHandle InterfaceHandle, byte PipeID);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_WritePipe(SafeWinUsbInterfaceHandle InterfaceHandle, byte PipeID, Byte[] Buffer, int BufferLength, out int LengthTransferred, IntPtr pOVERLAPPED);
		[DllImport(WIN_USB_DLL, SetLastError = true)]
		private static extern bool WinUsb_WritePipe(SafeWinUsbInterfaceHandle InterfaceHandle, byte PipeID, IntPtr pBuffer, int BufferLength, out int LengthTransferred, IntPtr pOVERLAPPED);
		//[DllImport(WIN_USB_DLL, SetLastError = true)]
		//private static extern bool WinUsb_SetPipePolicy(SafeWinUsbInterfaceHandle InterfaceHandle, byte PipeID, UInt32 PolicyType, UInt32 ValueLength, ref Byte Value);
		SafeFileHandle DeviceHandle;
		private SafeWinUsbInterfaceHandle[] InterfaceHandles = new SafeWinUsbInterfaceHandle[0];
		private int[] EndpointToInterfaceIn = new int[0];
		private int[] EndpointToInterfaceOut = new int[0];
		public IUsbDeviceRegistry Registry { get; private set; }

		public WinUsbDevice(String path, WinUsbRegistry registry) {
			this.Registry = registry;
			DeviceHandle = Kernel32.CreateFile(path,
				NativeFileAccess.FILE_GENERIC_WRITE | NativeFileAccess.FILE_GENERIC_READ,
				NativeFileShare.FILE_SHARE_WRITE | NativeFileShare.FILE_SHARE_READ,
				IntPtr.Zero,
				NativeFileMode.OPEN_EXISTING,
				NativeFileFlag.FILE_ATTRIBUTE_NORMAL | NativeFileFlag.FILE_FLAG_OVERLAPPED,
				IntPtr.Zero);
			if (DeviceHandle.IsInvalid || DeviceHandle.IsClosed) throw new Win32Exception();
			ThreadPool.BindHandle(DeviceHandle);
			SafeWinUsbInterfaceHandle InterfaceHandle;
			if (!WinUsb_Initialize(DeviceHandle, out InterfaceHandle)) throw new Win32Exception();
			if (InterfaceHandle.IsInvalid || InterfaceHandle.IsClosed) throw new Win32Exception();
			InterfaceHandles = new SafeWinUsbInterfaceHandle[1] { InterfaceHandle };
			foreach (UsbInterfaceInfo ifinfo in UsbDeviceInfo.FromDevice(this).FindConfiguration(Configuration).Interfaces) {
				foreach (UsbEndpointDescriptor epinfo in ifinfo.Endpoints) {
					int epidx = epinfo.EndpointAddress & 0x7F;
					if ((epinfo.EndpointAddress & 0x80) != 0) {
						if (EndpointToInterfaceIn.Length <= epidx) Array.Resize(ref EndpointToInterfaceIn, epidx + 1);
						EndpointToInterfaceIn[epidx] = ifinfo.Descriptor.InterfaceNumber;
					} else {
						if (EndpointToInterfaceOut.Length <= epidx) Array.Resize(ref EndpointToInterfaceOut, epidx + 1);
						EndpointToInterfaceOut[epidx] = ifinfo.Descriptor.InterfaceNumber;
					}
				}
			}
		}

		public void ClaimInterface(int interfaceID) {
			GetInterfaceHandle(interfaceID);
		}
		public void ReleaseInterface(int interfaceID) {
			if (interfaceID == 0) return;
			if (InterfaceHandles.Length < interfaceID || InterfaceHandles[interfaceID] == null) return;
			InterfaceHandles[interfaceID].Close();
			InterfaceHandles[interfaceID] = null;
		}
		void IUsbDevice.ResetDevice() {
			throw new NotSupportedException();
		}
		private SafeWinUsbInterfaceHandle GetInterfaceHandle(int interfaceID) {
			if (interfaceID == 0) return InterfaceHandles[0];
			if (interfaceID < 0 || interfaceID > 255) throw new ArgumentOutOfRangeException("interfaceID");
			if (InterfaceHandles.Length > interfaceID && InterfaceHandles[interfaceID] != null) return InterfaceHandles[interfaceID];
			SafeWinUsbInterfaceHandle ih;
			if (!WinUsb_GetAssociatedInterface(InterfaceHandles[0], (Byte)(interfaceID - 1), out ih) || ih.IsInvalid || ih.IsClosed)
				throw new Win32Exception();
			if (InterfaceHandles.Length <= interfaceID) Array.Resize(ref InterfaceHandles, interfaceID + 1);
			InterfaceHandles[interfaceID] = ih;
			return ih;
		}
		private SafeWinUsbInterfaceHandle GetInterfaceHandleForEndpoint(int epID) {
			int epidx = epID & 0x7F;
			if ((epID & 0x80) != 0) {
				if (EndpointToInterfaceIn.Length <= epidx) throw new ArgumentOutOfRangeException("endpoint");
				return GetInterfaceHandle(EndpointToInterfaceIn[epidx]);
			} else {
				if (EndpointToInterfaceOut.Length <= epidx) throw new ArgumentOutOfRangeException("endpoint");
				return GetInterfaceHandle(EndpointToInterfaceOut[epidx]);
			}
		}

		protected override void Dispose(Boolean disposing) {
			foreach (SafeWinUsbInterfaceHandle ih in InterfaceHandles) if (ih != null) ih.Close();
			if (disposing && DeviceHandle != null) DeviceHandle.Close();
		}

		public override Byte Configuration {
			get { return base.Configuration; }
			set {
				if (value == base.Configuration) return;
				throw new NotSupportedException();
			}
		}

		SafeWinUsbInterfaceHandle PrepareControlTransfer(UsbControlRequestType requestType, short index, ref Byte[] buffer, int offset, int length) {
			if (buffer == null) buffer = new Byte[0];
			if (offset < 0 || length < 0 || length > short.MaxValue || offset + length > buffer.Length) throw new ArgumentOutOfRangeException("length");
			SafeWinUsbInterfaceHandle ih = InterfaceHandles[0];
			switch ((UsbControlRequestType)requestType & UsbControlRequestType.RecipMask) {
				case UsbControlRequestType.RecipInterface: ih = GetInterfaceHandle(index & 0xff); break;
				case UsbControlRequestType.RecipEndpoint: ih = GetInterfaceHandleForEndpoint(index & 0xff); break;
				case UsbControlRequestType.RecipOther: break;
			}
			return ih;
		}
		public override unsafe int ControlTransfer(UsbControlRequestType requestType, byte request, short value, short index, byte[] buffer, int offset, int length) {
			SafeWinUsbInterfaceHandle ih = PrepareControlTransfer(requestType, index, ref buffer, offset, length);
			fixed (Byte* b = buffer) {
				if (!WinUsb_ControlTransfer(ih,
					new UsbSetupPacket((byte)requestType, request, value, index, (short)length),
					(IntPtr)(b + offset), length, out length, IntPtr.Zero))
					throw new Win32Exception();
				return length;
			}
		}
		public override unsafe IAsyncResult BeginControlTransfer(UsbControlRequestType requestType, byte request, short value, short index, byte[] buffer, int offset, int length, AsyncCallback callback, Object state) {
			SafeWinUsbInterfaceHandle ih = PrepareControlTransfer(requestType, index, ref buffer, offset, length);
			WindowsOverlappedAsyncResult ar = new WindowsOverlappedAsyncResult(callback, state);
			try {
				fixed (Byte* b = buffer) {
					Boolean success = WinUsb_ControlTransfer(ih,
						new UsbSetupPacket((byte)requestType, request, value, index, (short)length),
						(IntPtr)(b + offset), length, out length, (IntPtr)ar.PackOverlapped(buffer));
					ar.SyncResult(success, length);
					return ar;
				}
			} catch {
				ar.ErrorCleanup();
				throw;
			}
		}
		public override int EndControlTransfer(IAsyncResult asyncResult) {
			return ((WindowsOverlappedAsyncResult)asyncResult).Complete();
		}

		public unsafe override int GetDescriptor(byte descriptorType, byte index, short langId, byte[] buffer, int offset, int length) {
			if (length > short.MaxValue || offset < 0 || length < 0 || offset + length > buffer.Length) throw new ArgumentOutOfRangeException("length");
			fixed (Byte* b = buffer) {
				if (!WinUsb_GetDescriptor(InterfaceHandles[0], descriptorType, index, (ushort)langId, (IntPtr)(b + offset), length, out length))
					throw new Win32Exception();
			}
			return length;
		}

		unsafe Boolean BeginPipeTransfer(byte endpoint, byte[] buffer, int offset, ref int length, IntPtr overlapped) {
			if (offset < 0 || length < 0 || offset + length > buffer.Length) throw new ArgumentOutOfRangeException("length", "The specified offset and length exceed the buffer length");
			SafeWinUsbInterfaceHandle ih = GetInterfaceHandleForEndpoint(endpoint);
			fixed (Byte* b = buffer) {
				if ((endpoint & (Byte)UsbControlRequestType.EndpointMask) == (Byte)UsbControlRequestType.EndpointOut)
					return WinUsb_WritePipe(ih, endpoint, (IntPtr)(b + offset), length, out length, overlapped);
				else
					return WinUsb_ReadPipe(ih, endpoint, (IntPtr)(b + offset), length, out length, overlapped);
			}
		}
		public override int PipeTransfer(byte endpoint, byte[] buffer, int offset, int length) {
			if (!BeginPipeTransfer(endpoint, buffer, offset, ref length, IntPtr.Zero)) throw new Win32Exception();
			return length;
		}
		public override unsafe IAsyncResult BeginPipeTransfer(Byte endpoint, Byte[] buffer, int offset, int length, AsyncCallback callback, Object state) {
			WindowsOverlappedAsyncResult ar = new WindowsOverlappedAsyncResult(callback, state);
			try {
				Boolean success = BeginPipeTransfer(endpoint, buffer, offset, ref length, (IntPtr)ar.PackOverlapped(buffer));
				ar.SyncResult(success, length);
				return ar;
			} catch {
				ar.ErrorCleanup();
				throw;
			}
		}
		public override int EndPipeTransfer(IAsyncResult asyncResult) {
			return ((WindowsOverlappedAsyncResult)asyncResult).Complete();
		}

		public override void PipeReset(byte endpoint) {
			SafeWinUsbInterfaceHandle ih = GetInterfaceHandleForEndpoint(endpoint);
			WinUsb_ResetPipe(ih, endpoint);
		}
		public override void PipeAbort(byte endpoint) {
			SafeWinUsbInterfaceHandle ih = GetInterfaceHandleForEndpoint(endpoint);
			WinUsb_AbortPipe(ih, endpoint);
		}
	}
}