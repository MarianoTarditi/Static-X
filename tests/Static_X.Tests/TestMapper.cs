using AutoMapper;
using BLL.Mappings;
using Microsoft.Extensions.Logging;

namespace Static_X.Tests;

internal static class TestMapper
{
    // Reuse production profiles and keep license diagnostics visible; no test license is invented.
    private static readonly ILoggerFactory LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(
        logging => logging.AddConsole());

    internal static MapperConfiguration Configuration { get; } = new MapperConfiguration(
        configuration => configuration.AddMaps(typeof(StudentProfile).Assembly), LoggerFactory);

    internal static IMapper Instance { get; } = Configuration.CreateMapper();
}
