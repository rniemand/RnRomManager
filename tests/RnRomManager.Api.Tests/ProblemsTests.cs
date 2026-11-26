using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace RnRomManager.Api.Tests;

/// <summary>D3: the one-field <c>Validation</c> overload matches a single-entry dictionary.</summary>
public class ProblemsTests
{
    [Test]
    public void Validation_for_one_field_holds_that_error()
    {
        var ctx = new DefaultHttpContext { TraceIdentifier = "trace-1" };

        var result = (ProblemHttpResult)Problems.Validation(ctx, "name", "Required.");
        var details = (HttpValidationProblemDetails)result.ProblemDetails;

        Assert.That(result.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
        Assert.That(details.Errors, Has.Count.EqualTo(1));
        Assert.That(details.Errors["name"], Is.EqualTo(new[] { "Required." }));
        Assert.That(details.Extensions["code"], Is.EqualTo("ValidationFailed"));
    }
}
