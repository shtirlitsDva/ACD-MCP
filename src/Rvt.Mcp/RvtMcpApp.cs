using Autodesk.Revit.UI;

namespace Rvt.Mcp
{
    // Add-in entry point. Unlike the AutoCAD plugin (ACDMCP_START command),
    // the pipe starts automatically: Revit has no command line, and the
    // executor only does work when the agent actually sends something.
    //
    // Wiring order matters: ExternalEvent.Create and the UIApplication are
    // only available inside API context, and OnStartup gives us
    // UIControlledApplication (not UIApplication) — so the executor attaches
    // on the FIRST Idling event, where the sender is the real UIApplication.
    public sealed class RvtMcpApp : IExternalApplication
    {
        private RvtExecutor? _executor;
        private RvtPipeListener? _listener;
        private bool _attached;

        public Result OnStartup(UIControlledApplication application)
        {
            _executor = new RvtExecutor();
            _listener = new RvtPipeListener(
                _executor,
                application.ControlledApplication.VersionNumber);

            application.Idling += AttachOnFirstIdle;
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _listener?.Dispose();
            _listener = null;
            return Result.Succeeded;
        }

        private void AttachOnFirstIdle(object? sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
        {
            if (_attached) return;
            _attached = true;

            if (sender is UIApplication uiApp)
            {
                uiApp.Idling -= AttachOnFirstIdle;
                _executor!.Attach(uiApp, RvtJson.BuildOptions());
                _listener!.Start();
            }
        }
    }
}
