using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Rankoon.Hubs;

[Authorize]
public abstract class ModuleHub : Hub
{
}
