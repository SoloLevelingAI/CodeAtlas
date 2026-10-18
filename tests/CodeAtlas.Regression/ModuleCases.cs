using System.Text.Json;
using CodeAtlas.Composition;
using CodeAtlas.Contracts;
using CodeAtlas.Exploration;
using CodeAtlas.Host;
using CodeAtlas.Presentation;
using CodeAtlas.Runtime;
using CodeAtlas.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CodeAtlas.Regression;

public static partial class Program
{
    private static ModuleDescriptor Module(string id, string? parent = null) => new(id, id, "fixture module", new(0, 2, 0), parent);
    private static ModuleDescriptor Provider(string id, int major = 1, int minor = 2) => Module(id) with
    { Provides = [new("probe", typeof(IProbe), new(major, minor))] };
    private static ModuleDescriptor Consumer(string id = "consumer", bool optional = false, int major = 1, int minor = 0) => Module(id) with
    { Requires = [new("probe", typeof(IProbe), new(major, minor), optional)] };
    private static ModuleProfile Select(params string[] ids) => new("fixture", [.. ids]);

    private static async Task ModuleCompatibilityCases(string temporary, string rootDirectory)
    {
        var nested = new ModuleRegistry().Register(Module("root")).Register(Module("logical-child", "root"))
            .Register(Module("leaf", "logical-child")).Register(Module("not-selected", "root"));
        ResolvedProfile hierarchy = nested.Resolve(Select("leaf"));
        Check(hierarchy.Modules.Select(module => module.Id).SequenceEqual(["root", "logical-child", "leaf"]), "profile adds explicit ancestors in order without guessing dotted IDs");
        Check(hierarchy.Modules.All(module => module.Id != "not-selected"), "parent selection does not silently enable sibling or optional children");
        Throws<InvalidOperationException>(() => nested.Register(Module("root")), "duplicate module ID rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Module("orphan", "absent")).Resolve(Select("orphan")), "missing module parent rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Module("a", "b")).Register(Module("b", "a")).Resolve(Select("a")), "logical parent cycle rejected");
        Throws<InvalidOperationException>(() => nested.Resolve(Select("absent")), "unknown selected module rejected");
        Throws<InvalidOperationException>(() => nested.Resolve(new("empty", [])), "empty profile rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Module("a") with { Dependencies = [new("absent")] }).Resolve(Select("a")), "missing required module rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Module("a") with { Dependencies = [new("b")] })
            .Register(Module("b") with { Dependencies = [new("a")] }).Resolve(Select("a")), "module dependency cycle rejected");
        var optionalModule = new ModuleRegistry().Register(Module("a") with { Dependencies = [new("missing", true)] }).Resolve(Select("a"));
        Check(optionalModule.Modules.Length == 1, "absent optional module does not expand dependency closure");

        var capabilities = new ModuleRegistry().Register(Consumer()).Register(Provider("provider"));
        ResolvedProfile compatible = capabilities.Resolve(Select("consumer"));
        Check(compatible.Modules.Select(module => module.Id).SequenceEqual(["provider", "consumer"]), "compatible capability provider is activated before consumer");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Consumer()).Resolve(Select("consumer")), "missing required capability rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Consumer()).Register(Provider("v2", 2)).Resolve(Select("consumer")), "incompatible capability major version rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Consumer(minor: 3)).Register(Provider("v1", 1, 2)).Resolve(Select("consumer")), "insufficient capability minor version rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Consumer()).Register(Provider("one")).Register(Provider("two")).Resolve(Select("consumer")), "ambiguous automatic capability selection rejected");
        var alternatives = new ModuleRegistry().Register(Consumer()).Register(Provider("one")).Register(Provider("two"));
        Check(alternatives.Resolve(Select("consumer", "two")).Modules.Any(module => module.Id == "two"), "profile may explicitly choose one of several alternative providers");
        Throws<InvalidOperationException>(() => alternatives.Resolve(Select("one", "two")), "two selected providers of one capability rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Provider("one")).Register(Module("bad") with
            { Provides = [new("probe", typeof(IExplorerWorkspace), new(1, 0))] }).Resolve(Select("one")), "capability IDs cannot change interface identity");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Module("bad") with
            { Provides = [new("probe", typeof(string), new(1, 0))] }).Resolve(Select("bad")), "concrete class is not a capability contract");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Module("bad") with
            { Provides = [new("probe", typeof(IEnumerable<>), new(1, 0))] }).Resolve(Select("bad")), "open generic capability rejected");
        var optional = new ModuleRegistry().Register(Consumer(optional: true)).Register(Provider("off"));
        Check(optional.Resolve(Select("consumer")).Modules.Length == 1 && optional.Resolve(Select("consumer")).UnavailableOptionalCapabilities.Length == 1,
            "optional capability is reported unavailable and does not activate provider");
        Check(optional.Resolve(Select("consumer", "off")).UnavailableOptionalCapabilities.IsEmpty, "selected optional capability is checked and available");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Consumer(optional: true)).Register(Provider("v2", 2))
            .Resolve(Select("consumer", "v2")), "selected incompatible optional capability rejected");
        var view = Consumer() with { Views = [new("view", "Probe view", "web.main", "fixture", "probe")] };
        Check(new ModuleRegistry().Register(view).Register(Provider("p")).Resolve(Select("consumer")).Views.Length == 1,
            "view contribution resolves declared compatible data capability");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(view with { Requires = [] }).Register(Provider("p"))
            .Resolve(Select("consumer", "p")), "view cannot rely on an undeclared incidental capability");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(view with { Requires = [new("probe", typeof(IProbe), new(1, 0), true)] })
            .Resolve(Select("consumer")), "view requiring unavailable optional data is rejected");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(view).Register(view with { Id = "other" }).Register(Provider("p"))
            .Resolve(Select("consumer", "other")), "duplicate selected view contribution rejected");

        var services = new ServiceCollection();
        services.AddSingleton("keep");
        Throws<InvalidOperationException>(() => new ModuleRegistry().Register(Provider("p")).Apply(services, Select("p")), "declared capability without actual service wiring rejected");
        Check(services.Count == 1 && services[0].ServiceType == typeof(string), "failed profile activation leaves existing registrations unchanged");
        var actual = new ModuleRegistry().Register(Provider("p"), collection => collection.AddAtlas<IProbe, Probe>());
        actual.Apply(services, Select("p"));
        using (ServiceProvider activated = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }))
            Check(activated.GetRequiredService<IProbe>() is Probe && activated.GetRequiredService<ResolvedProfile>().Modules.Length == 1,
                "registry activates real legacy AddAtlas service and publishes resolved profile");
        Throws<InvalidOperationException>(() => actual.Apply(services, Select("p")), "profile cannot apply twice to same service collection");

        var headlessServices = new ServiceCollection();
        AtlasRuntime.CreateRegistry(temporary).Apply(headlessServices, AtlasRuntime.HeadlessProfile);
        using (ServiceProvider headless = headlessServices.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
            Check(headless.GetService<IExplorerWorkspace>() is null && headless.GetRequiredService<IAnalysisService>() is not null &&
                headless.GetRequiredService<ResolvedProfile>().Views.IsEmpty, "real headless profile omits exploration service and all views");
        using (ServiceProvider desktop = AtlasRuntime.Create(temporary))
            Check(desktop.GetRequiredService<IExplorerWorkspace>() is not null && desktop.GetRequiredService<IReportFileReader>() is not null,
                "legacy AtlasRuntime Create preserves desktop and CLI ports");
        var webServices = new ServiceCollection();
        ResolvedProfile webProfile = WebModules.Configure(webServices, temporary, "web");
        using (ServiceProvider web = webServices.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }))
        using (IServiceScope scope = web.CreateScope())
            Check(scope.ServiceProvider.GetRequiredService<GraphExplorerViewModel>() is not null && webProfile.Views.Single().Renderer == "atlas.explorer",
                "web view contribution is backed by real scoped explorer and workspace");
        Throws<NotSupportedException>(() => WebModules.Configure(new ServiceCollection(), temporary, "unknown"), "unknown host profile rejected before startup");

        var backend = new FakeWorkspace();
        var vm = new GraphExplorerViewModel(backend);
        await vm.LoadAsync();
        string before = JsonSerializer.Serialize(vm.Workspace!.Report);
        var map = new ExpandableMap(vm, webProfile);
        var root = map.Branches.Single(branch => branch.Node.FeatureId == "atlas.explore");
        map.Toggle(root);
        var navigation = map.Branches.Single(branch => branch.Node.FeatureId == "atlas.explore.navigation");
        map.Toggle(navigation);
        Check(map.Branches.Any(branch => branch.Depth == 2 && branch.Node.FeatureId == "atlas.explore.navigation.history"),
            "existing ExpandableMap renders nested runtime registry data");
        Check(map.Edges.IsEmpty && map.Branches.All(branch => branch.Node.SymbolId is null), "runtime declarations do not invent source relations or symbols");
        var index = new ReportIndex(vm.Workspace.Report);
        Check(index.Report.SchemaVersion == 1 && JsonSerializer.Serialize(index.Report) == before,
            "existing ReportIndex and module browsing preserve legacy schema 1 report exactly");
        string fixtureDirectory = Path.Combine(rootDirectory, "tests", "fixtures");
        string fixturePath = Path.Combine(fixtureDirectory, "legacy-no-features.json");
        byte[] original = await File.ReadAllBytesAsync(fixturePath);
        AnalysisReport legacy = await new JsonReportRepository(fixtureDirectory).LoadAsync("legacy-no-features")
            ?? throw new InvalidOperationException("missing legacy fixture");
        Check(legacy.Graph.Features.IsDefault && new ReportIndex(legacy).Search("Legacy").Count > 0,
            "schema 1 report without Features deserializes and remains searchable in old ReportIndex");
        byte[] afterRead = await File.ReadAllBytesAsync(fixturePath);
        Check(original.SequenceEqual(afterRead), "reading legacy report does not rewrite original bytes");
    }
}
