using System.Net;
using Entity;
using Xunit;

namespace Static_X.Tests;

public sealed class VideoLibraryHttpTests
{
    [Fact]
    public async Task Catalog_TrainerCanReadEmptyGym_StudentRequiresCoverage()
    {
        await using var app = await PaymentHttpApp.CreateAsync();
        using var trainer = await app.LoginAsync(AccountRole.Trainer);
        using var student = await app.LoginAsync(AccountRole.Student);
        Assert.Equal(HttpStatusCode.OK, (await trainer.GetAsync("/api/routines/videos")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync("/api/routines/videos")).StatusCode);
    }
}
