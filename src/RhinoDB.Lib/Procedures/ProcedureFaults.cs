namespace RhinoDB.Lib.Procedures;

static public class ProcedureFaults {
    static public void WriteToStandardError(ProcedureFault fault) =>
        Console.Error.WriteLine(
            $"RhinoDB: procedure '{fault.ProcedureName}' ({fault.Hash}) failed with {fault.Kind} for connection {fault.Session.ConnectionId.Value}: {fault.Exception}");
}
