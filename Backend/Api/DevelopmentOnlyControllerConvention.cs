using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Rankoon.Api;

[AttributeUsage(AttributeTargets.Class)]
public sealed class DevelopmentOnlyAttribute : Attribute;

public sealed class DevelopmentOnlyControllerConvention(IHostEnvironment environment) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        if (environment.IsDevelopment()) return;
        for (var index = application.Controllers.Count - 1; index >= 0; index--)
            if (application.Controllers[index].ControllerType.IsDefined(typeof(DevelopmentOnlyAttribute), inherit: true))
                application.Controllers.RemoveAt(index);
    }
}
