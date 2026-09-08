using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace BetterComputerUse;

internal static class AuthenticatedPipe
{
    internal static PipeSecurity Security()
    {
        var user = new SecurityIdentifier(Native.UserSid);
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }

    internal static NamedPipeServerStream CreateServer(string name) => NamedPipeServerStreamAcl.Create(name,
        PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, Security());
    // Explicit SID ACL permits same-user elevation transitions. Kernel PID/session/image/token
    // verification still determines which role may connect; network logons are denied outright.
}
