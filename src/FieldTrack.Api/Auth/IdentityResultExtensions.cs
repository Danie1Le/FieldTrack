using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace FieldTrack.Api.Auth;

/// <summary>Turns Identity's refusals (weak password, email in use) into a 400 the client can show.</summary>
internal static class IdentityResultExtensions
{
    /// <summary>A validation problem listing each Identity error under its code.</summary>
    public static ActionResult IdentityValidationProblem(this ControllerBase controller, IdentityResult result)
    {
        var errors = new ModelStateDictionary();
        foreach (var error in result.Errors)
        {
            errors.AddModelError(error.Code, error.Description);
        }

        return controller.ValidationProblem(errors);
    }
}
