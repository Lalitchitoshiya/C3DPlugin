
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(C3DPlugin.MyCommands))]
[assembly: ExtensionApplication(typeof(C3DPlugin.PluginExtension))]

namespace C3DPlugin
{
    public class MyCommands
    {
        [CommandMethod("MyGroup", "MyCommand", "MyCommandLocal", CommandFlags.Modal)]
        public void MyCommand() // This method can have any name
        {
            // Put your command code here
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            Autodesk.AutoCAD.EditorInput.Editor ed;
            if (doc != null)
            {
                ed = doc.Editor;
                ed.WriteMessage("Hello, this is your first command.");

            }
        }

        // Modal Command with pickfirst selection
        [CommandMethod("MyGroup", "MyPickFirst", "MyPickFirstLocal", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void MyPickFirst() // This method can have any name
        {
            PromptSelectionResult result = AcadApp.DocumentManager.MdiActiveDocument.Editor.GetSelection();
            if (result.Status == PromptStatus.OK)
            {
                // There are selected entities
                // Put your command using pickfirst set code here
            }
            else
            {
                // There are no selected entities
                // Put your command code here
            }
        }

        // Application Session Command with localized name
        [CommandMethod("MyGroup", "MySessionCmd", "MySessionCmdLocal", CommandFlags.Modal | CommandFlags.Session)]
        public void MySessionCmd() // This method can have any name
        {
            // Put your command code here
        }

        // LispFunction is similar to CommandMethod but it creates a lisp 
        // callable function. Many return types are supported not just string
        // or integer.
        [LispFunction("MyLispFunction", "MyLispFunctionLocal")]
        public int MyLispFunction(ResultBuffer args) // This method can have any name
        {
            // Put your command code here

            // Return a value to the AutoCAD Lisp Interpreter
            return 1;
        }

        [CommandMethod("WSPro", "WSPRO_IMPORT_PRESSURE", CommandFlags.Modal)]
        public void WsproImportPressure()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            Editor ed = doc.Editor;

            var nodesRes = ed.GetFileNameForOpen("\nSelect WSPro Nodes CSV (cssv4.CSV):");
            if (nodesRes.Status != PromptStatus.OK)
                return;

            var pipesRes = ed.GetFileNameForOpen("\nSelect WSPro Pipes CSV (PIPES_EXPORT.CSV):");
            if (pipesRes.Status != PromptStatus.OK)
                return;

            WsproImporter.ImportNetwork(ed, nodesRes.StringResult, pipesRes.StringResult);
        }

        [CommandMethod("WSPro", "WSPRO_DIAG_PRESSURE", CommandFlags.Modal)]
        public void WsproDiagPressure()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            Editor ed = doc.Editor;
            WsproImporter.DiagnosePressureApi(ed);
        }

        [CommandMethod("WSPro", "WSPRO_DIAG_PARTS", CommandFlags.Modal)]
        public void WsproDiagParts()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            Editor ed = doc.Editor;
            WsproImporter.DiagnosePartsList(ed);
        }

        [CommandMethod("WSPro", "WSPRO_DIAG_FITTINGS", CommandFlags.Modal)]
        public void WsproDiagFittings()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            Editor ed = doc.Editor;
            WsproImporter.DiagnoseFittings(ed);
        }
    }
}
