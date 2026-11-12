using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Pane.Media;

/// <summary>Media Foundation constants and small helpers shared by the H.264 encoder and decoder.</summary>
internal static class Mf
{
    // Media type attributes
    public static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    public static readonly Guid MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    public static readonly Guid MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    // Transform attributes / codec API properties
    public static readonly Guid LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    // Formats / categories / classes
    public static readonly Guid MediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid VideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid CLSID_MSH264EncoderMFT = new("6ca50344-051a-4ded-9779-a43305165e35");
    public static readonly Guid CLSID_MSH264DecoderMFT = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
    public static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    public const uint ENUM_FLAG_HARDWARE = 0x4, ENUM_FLAG_SORTANDFILTER = 0x40;
    public const int OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100, OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;

    public const int E_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    public const int E_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    public const int E_NOTACCEPTING = unchecked((int)0xC00D36B5);

    private static int _started;

    public static void Startup()
    {
        CoInitializeEx(IntPtr.Zero, 0 /*COINIT_MULTITHREADED*/);
        if (Interlocked.Exchange(ref _started, 1) == 0)
            MediaFactory.MFStartup(true).CheckError();
    }

    public static ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

    /// <summary>Create a built-in MFT directly by CLSID (bypasses enumeration).</summary>
    public static IMFTransform CreateByClsid(Guid clsid)
    {
        var iid = IID_IMFTransform;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out var p);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return new IMFTransform(p);
    }

    /// <summary>Set an ICodecAPI property; returns false if the codec does not support it.</summary>
    public static bool SetCodecValue(IMFTransform mft, Guid api, object value)
    {
        try
        {
            var obj = Marshal.GetObjectForIUnknown(mft.NativePointer);
            try
            {
                if (obj is not ICodecAPI codec) return false;
                return codec.SetValue(ref api, ref value) >= 0;
            }
            finally { Marshal.ReleaseComObject(obj); }
        }
        catch { return false; }
    }

    /// <summary>Copy an IMFSample's bytes into a managed array.</summary>
    public static byte[] SampleBytes(IMFSample sample)
    {
        using var buf = sample.ConvertToContiguousBuffer();
        buf.Lock(out var p, out _, out var len);
        try
        {
            var bytes = new byte[len];
            Marshal.Copy(p, bytes, 0, len);
            return bytes;
        }
        finally { buf.Unlock(); }
    }

    public static IMFSample MakeSample(int capacity, out IMFMediaBuffer buffer)
    {
        var sample = MediaFactory.MFCreateSample();
        buffer = MediaFactory.MFCreateMemoryBuffer(capacity);
        sample.AddBuffer(buffer);
        return sample;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coinit);
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid iid, out IntPtr ppv);
}

/// <summary>Just the ICodecAPI methods we need (vtable order matters; trailing methods are omitted).</summary>
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api, out object min, out object max, out object step);
    [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint count);
    [PreserveSig] int GetDefaultValue(ref Guid api, out object value);
    [PreserveSig] int GetValue(ref Guid api, out object value);
    [PreserveSig] int SetValue(ref Guid api, ref object value);
}
