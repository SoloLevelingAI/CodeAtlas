using System.Reflection;
using CodeAtlas.Contracts;
using CodeAtlas.Framework;
using Microsoft.Extensions.DependencyInjection;

[assembly: AtlasModule("atlas.runtime.registration", "应用组合入口", ArchitecturalRole.Composition)]

namespace CodeAtlas.Composition;

public static class AtlasRegistration
{
    // Typed registration is composition glue. It does not leak the concrete provider into consumers.
    public static IServiceCollection AddAtlas<TContract, TImplementation>(this IServiceCollection services,
        Func<IServiceProvider, TImplementation>? factory = null)
        where TContract : class
        where TImplementation : class, TContract
    {
        if (!typeof(TContract).IsInterface || !typeof(TImplementation).GetCustomAttributes<AtlasProvidesAttribute>()
            .Any(attribute => attribute.Contract == typeof(TContract)))
        {
            throw new InvalidOperationException($"{typeof(TImplementation).Name} 必须显式声明所提供的接口 {typeof(TContract).Name}。");
        }
        if (services.Any(descriptor => descriptor.ServiceType == typeof(TContract)))
        {
            throw new InvalidOperationException("契约已有提供方：" + typeof(TContract).Name);
        }
        return factory is null ? services.AddSingleton<TContract, TImplementation>()
            : services.AddSingleton<TContract>(provider => factory(provider));
    }

    /// <summary>
    /// 交互 ViewModel 不是能力端口，按 Blazor 电路使用 Scoped。
    /// 登记必须发生在本类型中，索引器才把它视为已批准的组合入口，而不是绕过 AddAtlas。
    /// </summary>
    public static IServiceCollection AddScopedViewModel<TViewModel>(this IServiceCollection services)
        where TViewModel : class => services.AddScoped<TViewModel>();

    /// <summary>
    /// 已解析的 Profile 是组合结果快照，不是可替换的端口实现。
    /// 只允许从本组合入口登记这一份单例。
    /// </summary>
    public static IServiceCollection AddResolvedProfile(this IServiceCollection services, ResolvedProfile profile) =>
        services.AddSingleton(profile);
}
