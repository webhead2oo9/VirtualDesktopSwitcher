using System;
using System.Linq;
using System.Reflection;
using System.Windows;

public sealed class VdCodecWorker : MarshalByRefObject
{
    public string[] Execute(string operation, string codecName)
    {
        WorkerResult result = new WorkerResult();

        Action action = delegate
        {
            result.Fields = ExecuteOnCurrentThread(operation, codecName);
        };

        Application app = Application.Current;
        if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }

        return result.Fields;
    }

    public override object InitializeLifetimeService()
    {
        return null;
    }

    private static string[] ExecuteOnCurrentThread(string operation, string codecName)
    {
        Assembly streamerAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, "VirtualDesktop.Streamer", StringComparison.OrdinalIgnoreCase));

        if (streamerAssembly == null)
        {
            throw new InvalidOperationException("VirtualDesktop.Streamer assembly is not loaded in AppDomain " + AppDomain.CurrentDomain.FriendlyName + ".");
        }

        Type settingsType = streamerAssembly.GetType("VirtualDesktop.Streamer.StreamerSettings", true);
        Type settingsBaseType = streamerAssembly.GetType("VirtualDesktop.Core.SettingsBase`1", true).MakeGenericType(settingsType);
        Type videoCodecType = streamerAssembly.GetType("VirtualDesktop.Net.VideoCodec", true);

        PropertyInfo defaultProperty = settingsBaseType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static);
        PropertyInfo preferredCodecProperty = settingsType.GetProperty("PreferredCodec", BindingFlags.Public | BindingFlags.Instance);

        if (defaultProperty == null || preferredCodecProperty == null)
        {
            throw new MissingMemberException("Could not find StreamerSettings.Default or PreferredCodec.");
        }

        object settings = defaultProperty.GetValue(null, null);
        object before = preferredCodecProperty.GetValue(settings, null);

        if (string.Equals(operation, "Get", StringComparison.OrdinalIgnoreCase))
        {
            string current = before.ToString();
            return new[] { "Get", current, current, current };
        }

        if (!string.Equals(operation, "Set", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unknown codec operation: " + operation);
        }

        object requested = Enum.Parse(videoCodecType, codecName, true);
        preferredCodecProperty.SetValue(settings, requested, null);
        object after = preferredCodecProperty.GetValue(settings, null);

        return new[] { "Set", before.ToString(), after.ToString(), after.ToString() };
    }

    private sealed class WorkerResult
    {
        public string[] Fields;
    }
}
