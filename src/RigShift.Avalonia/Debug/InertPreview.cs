using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RigShift.Core.Abstractions;

namespace RigShift.Avalonia.Debug;

/// <summary>
/// RIGSHIFT_PREVIEW_INERT=1 (Debug, with RIGSHIFT_PREVIEW_DISPLAYS): every button can be clicked for a look at the UI and
/// nothing outside the data folder changes. Reads pass through; the calls below that start, move or write something
/// return their default instead and are logged.
/// </summary>
internal static class InertPreview
{
    private static readonly Dictionary<Type, string[]> Blocked = new()
    {
        [typeof(IAppLauncher)] = ["Start", "StopAsync"],
        [typeof(IGameStarter)] = ["Start"],
        [typeof(IAutostart)] = ["SetEnabled", "UpgradeEntry"],
        [typeof(IDesktopIcons)] = ["Restore"],
        [typeof(IWallpapers)] = ["Show"],
        [typeof(IWindowRescuer)] = ["RescueOffscreenWindows"],
        [typeof(IWindowLayout)] = ["Place"],
        [typeof(IPowerController)] = ["SetKeepAwake"],
        [typeof(IDuckingPreference)] = ["Write"],
        [typeof(IAudioController)] = ["SetDefaultAsync", "SetVolumeAsync"],
        [typeof(ISurroundController)] = ["ApplyAsync"],
        [typeof(IGameSettingsFiles)] = ["SwapAsync"],
    };

    public static void Register(IServiceCollection services)
    {
        foreach ((Type type, string[] methods) in Blocked)
        {
            ServiceDescriptor? original = services.LastOrDefault(d => d.ServiceType == type);
            if (original is null)
            {
                continue;
            }

            services.Remove(original);
            services.Add(ServiceDescriptor.Singleton(type, sp => Wrap(type, Create(sp, original), methods)));
        }
    }

    private static object Create(IServiceProvider sp, ServiceDescriptor d) =>
        d.ImplementationInstance ?? d.ImplementationFactory?.Invoke(sp) ?? ActivatorUtilities.CreateInstance(sp, d.ImplementationType!);

    private static object Wrap(Type type, object target, string[] methods)
    {
        object proxy = typeof(DispatchProxy).GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)!
            .MakeGenericMethod(type, typeof(Inert)).Invoke(null, null)!;
        ((Inert)proxy).Target = target;
        ((Inert)proxy).Methods = methods;
        return proxy;
    }

#pragma warning disable CA1852 // DispatchProxy generates a subclass at run time
    public class Inert : DispatchProxy
#pragma warning restore CA1852
    {
        internal object Target { get; set; } = null!;

        internal string[] Methods { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (!Methods.Contains(targetMethod.Name))
            {
                try
                {
                    return targetMethod.Invoke(Target, args);
                }
                catch (TargetInvocationException ex) when (ex.InnerException is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
                    throw;
                }
            }

            Serilog.Log.Information("Inert preview: skipped {Type}.{Method}", targetMethod.DeclaringType?.Name, targetMethod.Name);
            Type result = targetMethod.ReturnType;
            if (result == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (result.IsGenericType && result.GetGenericTypeDefinition() == typeof(Task<>))
            {
                Type value = result.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(value)
                    .Invoke(null, [value.IsValueType ? Activator.CreateInstance(value) : null]);
            }

            return result.IsValueType && result != typeof(void) ? Activator.CreateInstance(result) : null;
        }
    }
}
