namespace NetTriage.Core;

/// <summary>
/// The deterministic detector set. Every entry is a syntactic signature matched against the
/// names a project actually references. EffortDays is the baseline added to a project's estimate
/// when the detector fires - it is a heuristic weight, not a measured cost.
/// </summary>
public static class DetectorCatalog
{
    public static readonly IReadOnlyList<Detector> All = new List<Detector>
    {
        // ---- Hard blockers: no supported equivalent on modern .NET -------------------------
        new("NT1001", "ASP.NET WebForms", Severity.Blocker, "UI",
            "WebForms has no port. The UI layer must be rebuilt on Blazor, Razor Pages, ASP.NET Core MVC or a JS front end. Scope this as a rewrite of the presentation layer, not a migration.",
            40, new[]
            {
                "System.Web.UI.Page", "System.Web.UI.UserControl", "System.Web.UI.MasterPage",
                "System.Web.UI.WebControls", "System.Web.UI.HtmlControls", "System.Web.UI.WebControls.WebParts",
                "System.Web.UI.ScriptManager",
            }),

        new("NT1002", "WCF service host (server side)", Severity.Blocker, "Transport",
            "Server-side WCF is not ported. Move to CoreWCF for SOAP parity, or gRPC/REST if clients can change. Inventory every endpoint and its bindings before committing.",
            25, new[]
            {
                "System.ServiceModel.ServiceHost", "System.ServiceModel.ServiceContractAttribute",
                "System.ServiceModel.OperationContractAttribute", "System.ServiceModel.ServiceBehaviorAttribute",
                "System.ServiceModel.Web.WebServiceHost", "System.ServiceModel.Description.ServiceEndpoint",
            }),

        new("NT1003", ".NET Remoting", Severity.Blocker, "Transport",
            ".NET Remoting does not exist on modern .NET and has no shim. Each remoting boundary becomes an explicit API call - usually gRPC, HTTP or a message queue.",
            20, new[]
            {
                "System.Runtime.Remoting", "System.Runtime.Remoting.RemotingServices",
                "System.Runtime.Remoting.Channels", "System.Runtime.Remoting.Lifetime.ILease",
                "System.Runtime.Remoting.Messaging",
            }),

        new("NT1004", "AppDomain creation", Severity.Blocker, "Runtime",
            "AppDomains are gone. Isolated loading must be redesigned using AssemblyLoadContext, separate processes, or containers. This is an architectural change, not a rename.",
            12, new[]
            {
                "AppDomain.CreateDomain", "AppDomainSetup", "AppDomain.Unload",
                "AppDomain.CurrentDomain.CreateInstanceAndUnwrap",
            }),

        new("NT1005", "Windows Workflow Foundation", Severity.Blocker, "Runtime",
            "Workflow Foundation was not ported. Workflow logic must be re-expressed in code or on a workflow engine such as Elsa or Temporal. Expect this to be the longest single item.",
            30, new[]
            {
                "System.Activities", "System.Activities.Statements", "System.Workflow",
                "WorkflowApplication", "WorkflowRuntime", "WorkflowInvoker",
            }),

        new("NT1006", "BinaryFormatter / binary serialization", Severity.Blocker, "Serialization",
            "BinaryFormatter is removed on modern .NET and unsafe by design. Replace with System.Text.Json, protobuf or MessagePack - and note that any persisted binary payload needs a data migration.",
            6, new[]
            {
                "BinaryFormatter", "System.Runtime.Serialization.Formatters.Binary",
                "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter",
                "System.Runtime.Serialization.IFormatter", "FormatterServices",
                "NetDataContractSerializer",
            }),

        new("NT1007", "Code Access Security", Severity.Blocker, "Security",
            "CAS is not supported on modern .NET. Security boundaries must be re-established with the hosting environment (containers, OS permissions, identity), not with permission attributes.",
            8, new[]
            {
                "System.Security.Permissions", "System.Security.Permissions.SecurityPermissionAttribute",
                "System.Security.Permissions.PrincipalPermissionAttribute", "System.Security.PermissionSet",
                "System.Security.Policy",
            }),

        new("NT1008", "ASMX / SOAP web services (System.Web.Services)", Severity.Blocker, "Transport",
            "ASMX is not ported. Move to CoreWCF for SOAP parity or to gRPC/REST. If external clients cannot change, CoreWCF is the lower-risk path.",
            15, new[]
            {
                "System.Web.Services", "System.Web.Services.WebService",
                "System.Web.Services.Protocols.SoapHttpClientProtocol", "System.Web.Services.WebMethodAttribute",
                "System.Web.Services.Protocols.SoapDocumentMethodAttribute",
            }),

        new("NT1009", "COM+ / System.EnterpriseServices", Severity.Blocker, "Interop",
            "COM+ serviced components have no cross-platform equivalent. Each component needs a decision: keep it on Windows, wrap it behind a service boundary, or reimplement.",
            15, new[]
            {
                "System.EnterpriseServices", "System.EnterpriseServices.ServicedComponent",
                "System.EnterpriseServices.TransactionAttribute", "System.EnterpriseServices.ContextUtil",
            }),

        // ---- Significant warnings ----------------------------------------------------------
        new("NT1010", "System.Drawing", Severity.Warning, "Imaging",
            "System.Drawing.Common is Windows-only on modern .NET. If the target is Linux containers, move to SkiaSharp, ImageSharp or Magick.NET. Fine to keep if the app stays on Windows.",
            8, new[]
            {
                "System.Drawing", "System.Drawing.Common", "System.Drawing.Imaging",
                "System.Drawing.Printing", "System.Drawing.Bitmap",
            }),

        new("NT1011", "COM interop", Severity.Warning, "Interop",
            "COM interop works on Windows only. Confirm whether the deployment target is Linux; if so, these calls need a replacement or must stay behind a Windows-hosted boundary.",
            10, new[]
            {
                "System.Runtime.InteropServices.ComImportAttribute", "System.Runtime.InteropServices.Marshal.GetActiveObject",
                "System.Runtime.InteropServices.COMException", "Type.GetTypeFromProgID",
                "System.Runtime.InteropServices.ComVisibleAttribute",
            }),

        new("NT1012", "Windows Registry access", Severity.Warning, "Platform",
            "The registry is Windows-only. Configuration that lives there needs to move to appsettings/environment configuration for a cross-platform target.",
            5, new[]
            {
                "Microsoft.Win32.Registry", "Microsoft.Win32.RegistryKey",
                "Microsoft.Win32.Registry.LocalMachine", "Microsoft.Win32.Registry.CurrentUser",
            }),

        new("NT1013", "WMI / System.Management", Severity.Warning, "Platform",
            "WMI is Windows-only and its .NET client support on modern .NET is limited. Identify which queries are actually needed and whether they can be replaced by direct APIs.",
            6, new[]
            {
                "System.Management", "System.Management.ManagementObjectSearcher",
                "System.Management.ManagementObject", "System.Management.ManagementClass",
            }),

        new("NT1014", "Thread.Abort / Suspend / Resume", Severity.Warning, "Threading",
            "Thread.Abort is not supported on modern .NET and throws PlatformNotSupportedException. Cancellation must be redesigned with CancellationToken.",
            4, new[]
            {
                "Thread.Abort", "Thread.Suspend", "Thread.Resume", "Thread.Interrupt",
            }),

        new("NT1015", "ASP.NET MVC 5 / Web API 2", Severity.Warning, "Web",
            "Migratable but substantial: System.Web.Mvc and System.Web.Http map onto ASP.NET Core MVC, but filters, routing, model binding and HttpContext all change shape.",
            20, new[]
            {
                "System.Web.Mvc", "System.Web.Http", "System.Web.Routing",
                "System.Web.Mvc.Controller", "System.Web.Http.ApiController",
                "System.Web.Mvc.ActionResult", "System.Web.Http.Filters",
            }),

        new("NT1016", "System.Web HttpContext", Severity.Warning, "Web",
            "HttpContext.Current and System.Web.HttpContext do not exist on ASP.NET Core. Access must be injected (IHttpContextAccessor) and request state passed explicitly.",
            8, new[]
            {
                "System.Web.HttpContext", "HttpContext.Current", "System.Web.HttpContextBase",
                "System.Web.HttpRequestBase", "System.Web.HttpResponseBase",
            }),

        new("NT1017", "System.Web.Caching / HttpRuntime", Severity.Warning, "Caching",
            "System.Web caching maps onto IMemoryCache or IDistributedCache, but the eviction and dependency semantics differ. Cache invalidation logic needs a careful review.",
            4, new[]
            {
                "System.Web.Caching", "System.Web.Caching.Cache", "HttpRuntime.Cache",
                "System.Web.Caching.CacheDependency",
            }),

        new("NT1018", "Global.asax / HttpApplication", Severity.Warning, "Web",
            "Application lifecycle hooks move into Program.cs middleware and the generic host. Anything done in Application_Start or an HTTP module has to be re-homed explicitly.",
            5, new[]
            {
                "System.Web.HttpApplication", "System.Web.IHttpModule", "System.Web.IHttpHandler",
                "System.Web.HttpApplicationState",
            }),

        new("NT1019", "Entity Framework 6", Severity.Warning, "Data",
            "EF6 to EF Core is a rewrite of the data layer, not a package swap: mapping, lazy loading, migrations and query translation all differ. EF Core 8+ targets modern .NET only, so EF6 cannot come along.",
            20, new[]
            {
                "System.Data.Entity", "System.Data.Entity.Database.SetInitializer",
                "System.Data.Entity.DbModelBuilder", "System.Data.Entity.Infrastructure",
                "System.Data.Entity.Core.Objects.ObjectContext", "System.Data.Entity.Migrations",
            }),

        new("NT1020", "System.Configuration ConfigurationManager", Severity.Warning, "Configuration",
            "ConfigurationManager is available but legacy. Web.config/app.config transforms do not carry over - plan a move to appsettings.json plus Microsoft.Extensions.Configuration.",
            3, new[]
            {
                "System.Configuration.ConfigurationManager", "ConfigurationManager.AppSettings",
                "ConfigurationManager.ConnectionStrings", "System.Configuration.ConfigurationSection",
                "System.Configuration.ConfigurationManager.ConnectionStrings",
            }),

        new("NT1021", "Assembly.LoadFrom / LoadFile", Severity.Warning, "Runtime",
            "Assembly.LoadFrom and LoadFile still exist but interact badly with the modern load context. Plugin loading should move to AssemblyLoadContext.",
            3, new[]
            {
                "Assembly.LoadFrom", "Assembly.LoadFile", "Assembly.LoadWithPartialName",
                "Assembly.ReflectionOnlyLoad",
            }),

        new("NT1022", "WCF client usage", Severity.Warning, "Transport",
            "Consuming WCF services is easier than hosting them - community client libraries exist - but bindings and security modes still need validation against modern .NET.",
            6, new[]
            {
                "System.ServiceModel.ChannelFactory", "System.ServiceModel.ClientBase",
                "System.ServiceModel.DuplexClientBase",
            }),

        new("NT1023", "MSMQ / System.Messaging", Severity.Warning, "Messaging",
            "MSMQ has no first-class modern .NET client. If messaging is load-bearing, plan a move to a broker (RabbitMQ, Azure Service Bus, Kafka) as part of the migration.",
            8, new[]
            {
                "System.Messaging", "System.Messaging.MessageQueue", "System.Messaging.Message",
            }),

        // ---- Informational: supported, but worth knowing -----------------------------------
        new("NT1024", "WPF desktop UI", Severity.Info, "UI",
            "WPF is supported on modern .NET but Windows-only. The port is usually mechanical; the constraint is that the deployment target stays Windows.",
            0, new[]
            {
                "PresentationFramework", "System.Windows.Window", "System.Windows.Controls",
                "System.Windows.Media", "System.Windows.Markup",
            }),

        new("NT1025", "WinForms desktop UI", Severity.Info, "UI",
            "WinForms is supported on modern .NET but Windows-only. Designer files and third-party controls are the usual friction points.",
            0, new[]
            {
                "System.Windows.Forms", "System.Windows.Forms.Form",
                "System.Windows.Forms.Application",
            }),

        new("NT1026", "P/Invoke into native libraries", Severity.Info, "Interop",
            "P/Invoke works cross-platform but the native library must exist for the target OS. Verify every DllImport resolves on Linux before promising a container deployment.",
            2, new[]
            {
                "System.Runtime.InteropServices.DllImportAttribute",
                "System.Runtime.InteropServices.LibraryImportAttribute",
                "System.Runtime.InteropServices.NativeLibrary",
            }),

        new("NT1027", "Reflection-based dynamic type loading", Severity.Info, "Runtime",
            "Type.GetType and Activator on string names keep working, but trimming and AOT will break them. Relevant only if single-file, trimming or Native AOT is planned.",
            2, new[]
            {
                "Type.GetType", "Activator.CreateInstance", "Assembly.GetExecutingAssembly",
                "Assembly.GetEntryAssembly",
            }),

        // ---- Project- and solution-level detectors -----------------------------------------
        // These carry no Names: they are raised by the project reader, not by source matching.
        new("NT2001", "Legacy (non-SDK) project format", Severity.Info, "Project",
            "Convert to SDK-style while still targeting the current framework first. That step is low risk, unlocks multi-targeting, and removes most build friction before the real migration starts.",
            1, Array.Empty<string>()),

        new("NT2002", "packages.config", Severity.Info, "Dependencies",
            "Move to PackageReference as part of the SDK-style conversion. Central Package Management then gives you one place to see version drift across the estate.",
            1, Array.Empty<string>()),

        new("NT2003", "Target framework is out of support", Severity.Blocker, "Lifecycle",
            "This runtime receives no further security patches. Treat it as the top-priority item: either migrate, or get an explicit, dated exception signed off by whoever owns the risk.",
            0, Array.Empty<string>()),

        new("NT2004", "Target framework reaches end of support soon", Severity.Warning, "Lifecycle",
            "Plan the upgrade now rather than in an emergency. Note that .NET 8 and .NET 9 share an end-of-support date, so landing on .NET 8 buys no extra runway.",
            0, Array.Empty<string>()),

        new("NT2005", "Vendored binary reference", Severity.Warning, "Dependencies",
            "A DLL referenced by path has no source and no upgrade path. Every one of these needs an owner decision: replace it, wrap it, or accept it as a permanent blocker.",
            2, Array.Empty<string>()),

        new("NT2006", "No test project in the solution", Severity.Info, "Testing",
            "Without tests there is no way to prove behaviour was preserved across the migration. Building a characterisation test harness is usually the cheapest risk reduction available.",
            0, Array.Empty<string>()),
    };

    private static readonly Dictionary<string, Detector> ByCode =
        All.ToDictionary(d => d.Code, StringComparer.Ordinal);

    public static Detector? ById(string code) => ByCode.TryGetValue(code, out var d) ? d : null;

    /// <summary>Detectors that only make sense when the deployment target is not Windows.</summary>
    public static readonly IReadOnlySet<string> CrossPlatformSensitive =
        new HashSet<string>(StringComparer.Ordinal) { "NT1010", "NT1011", "NT1012", "NT1013", "NT1024", "NT1025" };
}
