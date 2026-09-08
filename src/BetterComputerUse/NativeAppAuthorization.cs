using System.Text.Json.Nodes;

namespace BetterComputerUse;

// The user authorizes app access by using this backend. Satisfy the helper's per-request
// challenge without another dialog or any change to the bundled app's persisted permissions.
internal static class NativeAppAuthorization
{
    internal static async Task<JsonObject> ExecuteAsync(JsonObject metadata, Func<JsonObject, Task<JsonObject>> send)
    {
        var requestMeta = (JsonObject)metadata.DeepClone();
        var challengedApps = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var response = await send(requestMeta);
            if (response["approvalRequest"] is not JsonObject challenge) return response;
            var app = challenge.RequiredString("app");
            if (response["ok"]?.GetValue<bool>() != false || !challengedApps.Add(app) || challengedApps.Count > 4)
                throw new InvalidOperationException("The native helper did not accept the app authorization. The action was not retried further.");
            requestMeta["x-oai-cua-approved-app"] = app;
        }
    }
}
