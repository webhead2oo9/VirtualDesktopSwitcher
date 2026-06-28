using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using EasyHook;

public sealed class VdCodecPayload : IEntryPoint
{
    private readonly string operation;
    private readonly string codecName;
    private readonly string resultPath;

    public VdCodecPayload(RemoteHooking.IContext context, string operation, string codecName, string resultPath)
    {
        this.operation = operation;
        this.codecName = codecName;
        this.resultPath = resultPath;
    }

    public void Run(RemoteHooking.IContext context, string operation, string codecName, string resultPath)
    {
        try
        {
            Execute();
        }
        catch (Exception ex)
        {
            WriteResult("ERROR", ex.ToString());
        }
    }

    private void Execute()
    {
        AppDomain defaultDomain = GetDefaultAppDomain();
        string workerPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "VdCodecWorker.dll");

        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException("Missing worker assembly.", workerPath);
        }

        VdCodecWorker worker = (VdCodecWorker)defaultDomain.CreateInstanceFromAndUnwrap(
            workerPath,
            typeof(VdCodecWorker).FullName);

        string[] fields = worker.Execute(operation, codecName);
        WriteResult("OK", fields);
    }

    private void WriteResult(string status, params string[] fields)
    {
        // Write to a sibling temp file and atomically rename it into place, so the
        // rotator never reads a half-written line (a partial base64 field would
        // throw on decode and surface a spurious error after a successful switch).
        string tempPath = resultPath + ".tmp";
        using (StreamWriter writer = new StreamWriter(tempPath, false, Encoding.UTF8))
        {
            writer.Write(status);
            foreach (string field in fields)
            {
                writer.Write('\t');
                writer.Write(Encode(field));
            }
            writer.WriteLine();
        }

        if (File.Exists(resultPath))
        {
            File.Delete(resultPath);
        }
        File.Move(tempPath, resultPath);
    }

    private static string Encode(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
    }

    private static AppDomain GetDefaultAppDomain()
    {
        Type hostType = Type.GetTypeFromCLSID(new Guid("CB2F6723-AB3A-11D2-9C40-00C04FA30A3E"));
        ICorRuntimeHost host = (ICorRuntimeHost)Activator.CreateInstance(hostType);
        host.Start();

        object domain;
        host.GetDefaultDomain(out domain);
        return (AppDomain)domain;
    }

    [ComImport]
    [Guid("CB2F6722-AB3A-11D2-9C40-00C04FA30A3E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ICorRuntimeHost
    {
        void CreateLogicalThreadState();
        void DeleteLogicalThreadState();
        void SwitchInLogicalThreadState(IntPtr fiberCookie);
        void SwitchOutLogicalThreadState(out IntPtr fiberCookie);
        void LocksHeldByLogicalThread(out int count);
        void MapFile(IntPtr fileHandle, out IntPtr mapAddress);
        void GetConfiguration(out IntPtr configuration);
        void Start();
        void Stop();
        void CreateDomain([MarshalAs(UnmanagedType.LPWStr)] string friendlyName, object identityArray, [MarshalAs(UnmanagedType.Interface)] out object appDomain);
        void GetDefaultDomain([MarshalAs(UnmanagedType.Interface)] out object appDomain);
    }
}
