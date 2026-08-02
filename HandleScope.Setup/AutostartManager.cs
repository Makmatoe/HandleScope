using System.Runtime.InteropServices;
using System.Security;
using System.Xml;
using System.Xml.Linq;

namespace HandleScope.Setup;

internal enum AutostartState
{
    Absent,
    Enabled,
    Disabled,
    Unexpected
}

internal interface IAutostartManager
{
    AutostartState Inspect(string executablePath, string workingDirectory);

    void Enable(string executablePath, string workingDirectory);

    void Remove(string executablePath, string workingDirectory);
}

internal sealed class TaskSchedulerAutostartManager : IAutostartManager
{
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskStateDisabled = 1;
    private const int ErrorFileNotFound = unchecked((int)0x80070002);
    private readonly SetupIdentity _identity;
    private readonly string _folderPath;

    internal TaskSchedulerAutostartManager(SetupIdentity identity)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _folderPath = $"\\HandleScope\\{identity.Sid}";
    }

    public AutostartState Inspect(
        string executablePath,
        string workingDirectory)
    {
        dynamic? service = null;
        dynamic? folder = null;
        dynamic? task = null;
        try
        {
            service = Connect();
            folder = TryGetFolder(service, _folderPath);
            if (folder is null)
            {
                return AutostartState.Absent;
            }
            task = TryGetTask(folder, "Local API");
            if (task is null)
            {
                return AutostartState.Absent;
            }

            string xml = task.Xml;
            if (!IsExpectedTask(xml, executablePath, workingDirectory))
            {
                return AutostartState.Unexpected;
            }
            int state = task.State;
            return state == TaskStateDisabled || IsTaskXmlDisabled(xml)
                ? AutostartState.Disabled
                : AutostartState.Enabled;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or
                UnauthorizedAccessException or XmlException)
        {
            throw new SetupSafetyException(
                "The HandleScope autostart task could not be inspected safely.",
                exception);
        }
        finally
        {
            ReleaseCom(task);
            ReleaseCom(folder);
            ReleaseCom(service);
        }
    }

    public void Enable(string executablePath, string workingDirectory)
    {
        var state = Inspect(executablePath, workingDirectory);
        if (state == AutostartState.Unexpected)
        {
            throw new SetupSafetyException(
                "An unexpected task occupies the HandleScope autostart identity.");
        }

        dynamic? service = null;
        dynamic? folder = null;
        try
        {
            service = Connect();
            folder = GetOrCreateFolder(service);
            var xml = CreateTaskXml(executablePath, workingDirectory);
            folder.RegisterTask(
                "Local API",
                xml,
                TaskCreateOrUpdate,
                _identity.Sid,
                null,
                TaskLogonInteractiveToken);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or
                UnauthorizedAccessException)
        {
            throw new SetupOperationException(
                "Windows could not enable the limited per-user autostart task.",
                exception);
        }
        finally
        {
            ReleaseCom(folder);
            ReleaseCom(service);
        }

        if (Inspect(executablePath, workingDirectory) != AutostartState.Enabled)
        {
            throw new SetupOperationException(
                "The limited per-user autostart task did not validate after registration.");
        }
    }

    public void Remove(string executablePath, string workingDirectory)
    {
        var state = Inspect(executablePath, workingDirectory);
        if (state == AutostartState.Absent)
        {
            return;
        }
        if (state == AutostartState.Unexpected)
        {
            throw new SetupSafetyException(
                "Setup refused to remove an unexpected HandleScope task.");
        }

        dynamic? service = null;
        dynamic? folder = null;
        try
        {
            service = Connect();
            folder = service.GetFolder(_folderPath);
            folder.DeleteTask("Local API", 0);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or
                UnauthorizedAccessException)
        {
            throw new SetupOperationException(
                "Windows could not remove the HandleScope autostart task.",
                exception);
        }
        finally
        {
            ReleaseCom(folder);
            ReleaseCom(service);
        }

        if (Inspect(executablePath, workingDirectory) != AutostartState.Absent)
        {
            throw new SetupOperationException(
                "The HandleScope autostart task still exists after removal.");
        }
    }

    internal bool IsExpectedTask(
        string xml,
        string executablePath,
        string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        var document = XDocument.Parse(
            xml,
            LoadOptions.None);
        XNamespace taskNamespace =
            "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var root = document.Root;
        if (root?.Name != taskNamespace + "Task")
        {
            return false;
        }

        var principals = root.Element(taskNamespace + "Principals")?
            .Elements(taskNamespace + "Principal").ToArray();
        var actions = root.Element(taskNamespace + "Actions")?
            .Elements().ToArray();
        var triggers = root.Element(taskNamespace + "Triggers")?
            .Elements().ToArray();
        if (principals is not { Length: 1 } ||
            actions is not { Length: 1 } ||
            triggers is not { Length: 1 } ||
            actions[0].Name != taskNamespace + "Exec" ||
            triggers[0].Name != taskNamespace + "LogonTrigger")
        {
            return false;
        }

        var principal = principals[0];
        var action = actions[0];
        var trigger = triggers[0];
        var command = action.Element(taskNamespace + "Command")?.Value;
        var arguments = action.Element(taskNamespace + "Arguments")?.Value;
        var actualWorkingDirectory = action
            .Element(taskNamespace + "WorkingDirectory")?.Value;
        var principalUser = principal.Element(taskNamespace + "UserId")?.Value;
        var triggerUser = trigger.Element(taskNamespace + "UserId")?.Value;
        var runLevel = principal.Element(taskNamespace + "RunLevel")?.Value;
        var settingsElements = root.Elements(taskNamespace + "Settings").ToArray();
        if (settingsElements.Length != 1)
        {
            return false;
        }
        var settings = settingsElements[0];
        var nativeIdentity = string.Equals(
            triggerUser,
            _identity.Sid,
            StringComparison.OrdinalIgnoreCase);
        var legacyIdentity = string.Equals(
            triggerUser,
            _identity.Name,
            StringComparison.OrdinalIgnoreCase);
        var batteryPolicyIsExpected = nativeIdentity
            ? HasSingleValue(
                    settings,
                    taskNamespace + "DisallowStartIfOnBatteries",
                    "false") &&
                HasSingleValue(
                    settings,
                    taskNamespace + "StopIfGoingOnBatteries",
                    "false")
            : legacyIdentity &&
                IsLegacyBatteryDefault(
                    settings,
                    taskNamespace + "DisallowStartIfOnBatteries") &&
                IsLegacyBatteryDefault(
                    settings,
                    taskNamespace + "StopIfGoingOnBatteries");
        var restartElements = settings
            .Elements(taskNamespace + "RestartOnFailure")
            .ToArray();
        var restartPolicyIsExpected = restartElements.Length == 1 &&
            restartElements[0].Elements().Count() == 2 &&
            HasSingleValue(
                restartElements[0],
                taskNamespace + "Interval",
                "PT1M") &&
            HasSingleValue(
                restartElements[0],
                taskNamespace + "Count",
                "3");
        var behavioralCoreIsExpected =
            HasSingleValue(
                settings,
                taskNamespace + "MultipleInstancesPolicy",
                "IgnoreNew") &&
            HasSingleValue(
                settings,
                taskNamespace + "StartWhenAvailable",
                "true") &&
            HasSingleValue(
                settings,
                taskNamespace + "ExecutionTimeLimit",
                "PT0S") &&
            restartPolicyIsExpected;
        return string.Equals(
                principalUser,
                _identity.Sid,
                StringComparison.OrdinalIgnoreCase) &&
            principal.Element(taskNamespace + "LogonType")?.Value ==
                "InteractiveToken" &&
            runLevel is null or "LeastPrivilege" &&
            (nativeIdentity || legacyIdentity) &&
            batteryPolicyIsExpected &&
            behavioralCoreIsExpected &&
            IsCanonicalOptionalBoolean(
                trigger,
                taskNamespace + "Enabled") &&
            IsExactPath(command, executablePath) &&
            string.IsNullOrEmpty(arguments) &&
            IsExactPath(actualWorkingDirectory, workingDirectory) &&
            IsCanonicalOptionalBoolean(
                settings,
                taskNamespace + "Enabled");
    }

    private static bool IsExactPath(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }
        try
        {
            return string.Equals(
                SetupPaths.Normalize(actual),
                SetupPaths.Normalize(expected),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or
                PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsCanonicalOptionalBoolean(
        XElement parent,
        XName name)
    {
        var elements = parent.Elements(name).ToArray();
        return elements.Length == 0 ||
            elements is [{ Value: "true" or "false" }];
    }

    private static bool IsLegacyBatteryDefault(XElement settings, XName name)
    {
        var elements = settings.Elements(name).ToArray();
        return elements.Length == 0 || elements is [{ Value: "true" }];
    }

    private static bool HasSingleValue(
        XElement parent,
        XName name,
        string value)
    {
        var elements = parent.Elements(name).ToArray();
        return elements.Length == 1 && elements[0].Value == value;
    }

    internal static bool IsTaskXmlDisabled(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        XNamespace taskNamespace =
            "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return document.Root?
                .Element(taskNamespace + "Triggers")?
                .Elements()
                .Any(trigger =>
                    trigger.Element(taskNamespace + "Enabled")?.Value == "false") ==
                true ||
            document.Root?
                .Element(taskNamespace + "Settings")?
                .Element(taskNamespace + "Enabled")?.Value == "false";
    }

    internal void ValidateTaskXmlWithScheduler(string xml)
    {
        dynamic? service = null;
        dynamic? definition = null;
        try
        {
            service = Connect();
            definition = service.NewTask(0);
            definition.XmlText = xml;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or
                UnauthorizedAccessException)
        {
            throw new SetupSafetyException(
                "Windows Task Scheduler rejected the compiled task XML.",
                exception);
        }
        finally
        {
            ReleaseCom(definition);
            ReleaseCom(service);
        }
    }

    private dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)
            ?? throw new SetupOperationException(
                "Windows Task Scheduler is unavailable.");
        dynamic service = Activator.CreateInstance(type)
            ?? throw new SetupOperationException(
                "Windows Task Scheduler could not be created.");
        service.Connect();
        return service;
    }

    private dynamic GetOrCreateFolder(dynamic service)
    {
        dynamic? productFolder = null;
        dynamic? root = null;
        try
        {
            root = service.GetFolder("\\");
            productFolder = TryGetFolder(service, "\\HandleScope");
            if (productFolder is null)
            {
                productFolder = root.CreateFolder("HandleScope");
            }
            var sidFolder = TryGetFolder(service, _folderPath);
            if (sidFolder is not null)
            {
                return sidFolder;
            }
            return productFolder.CreateFolder(_identity.Sid);
        }
        finally
        {
            ReleaseCom(productFolder);
            ReleaseCom(root);
        }
    }

    private static dynamic? TryGetFolder(dynamic service, string path)
    {
        try
        {
            return service.GetFolder(path);
        }
        catch (COMException exception) when (exception.HResult == ErrorFileNotFound)
        {
            return null;
        }
    }

    private static dynamic? TryGetTask(dynamic folder, string name)
    {
        try
        {
            return folder.GetTask(name);
        }
        catch (COMException exception) when (exception.HResult == ErrorFileNotFound)
        {
            return null;
        }
    }

    internal string CreateTaskXml(string executablePath, string workingDirectory)
    {
        using var writer = new StringWriter(
            System.Globalization.CultureInfo.InvariantCulture);
        using (var xml = XmlWriter.Create(
                   writer,
                   new XmlWriterSettings
                   {
                       OmitXmlDeclaration = false,
                       Indent = false
                   }))
        {
            const string ns =
                "http://schemas.microsoft.com/windows/2004/02/mit/task";
            xml.WriteStartElement("Task", ns);
            xml.WriteAttributeString("version", "1.4");
            xml.WriteStartElement("RegistrationInfo", ns);
            xml.WriteElementString(
                "Description",
                ns,
                "Runs the restricted HandleScope Roblox automation API for this user.");
            xml.WriteEndElement();
            xml.WriteStartElement("Triggers", ns);
            xml.WriteStartElement("LogonTrigger", ns);
            xml.WriteElementString("Enabled", ns, "true");
            xml.WriteElementString("UserId", ns, _identity.Sid);
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("Principals", ns);
            xml.WriteStartElement("Principal", ns);
            xml.WriteAttributeString("id", "Author");
            xml.WriteElementString("UserId", ns, _identity.Sid);
            xml.WriteElementString("LogonType", ns, "InteractiveToken");
            xml.WriteElementString("RunLevel", ns, "LeastPrivilege");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("Settings", ns);
            xml.WriteElementString("MultipleInstancesPolicy", ns, "IgnoreNew");
            xml.WriteElementString(
                "DisallowStartIfOnBatteries",
                ns,
                "false");
            xml.WriteElementString(
                "StopIfGoingOnBatteries",
                ns,
                "false");
            xml.WriteElementString("StartWhenAvailable", ns, "true");
            xml.WriteElementString("Enabled", ns, "true");
            xml.WriteElementString("ExecutionTimeLimit", ns, "PT0S");
            xml.WriteStartElement("RestartOnFailure", ns);
            xml.WriteElementString("Interval", ns, "PT1M");
            xml.WriteElementString("Count", ns, "3");
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteStartElement("Actions", ns);
            xml.WriteAttributeString("Context", "Author");
            xml.WriteStartElement("Exec", ns);
            xml.WriteElementString("Command", ns, executablePath);
            xml.WriteElementString("WorkingDirectory", ns, workingDirectory);
            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
        }
        return writer.ToString();
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }
}
