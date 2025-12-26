using System.Reflection;
using System.Reflection.Emit;

namespace SeparateProcess;

public class ProxyGenerator
{
    private static readonly Type managerType = typeof(ProcessManager);
    private readonly Type _virtualType;
    private readonly FieldInfo _managerField;
    private readonly TypeBuilder _typeBuilder;

    public ProxyGenerator(Type virtualType)
    {
        _virtualType = virtualType;
        var assemblyName = new AssemblyName("ProxyAssembly");
        var assembly = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("ProxyModule");
        _typeBuilder = module.DefineType("Proxy" + virtualType.Name, TypeAttributes.Public | TypeAttributes.Class, virtualType, null);
        _managerField = _typeBuilder.DefineField("_manager", typeof(object), FieldAttributes.Private);
    }

    public static TService CreateProxy<TService>(ProcessManager manager) where TService : class, IBackgroundService
    {
        var generator = new ProxyGenerator(typeof(TService));
        return (TService)generator.GenerateProxy(manager);
    }

    private object GenerateProxy(ProcessManager manager)
    {
        var type = GenerateProxyType();
        return Activator.CreateInstance(type, manager)!;
    }

    private Type GenerateProxyType()
    {
        // Constructor
        var ctor = _typeBuilder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, [typeof(object)]);
        var il = ctor.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _virtualType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _managerField);
        il.Emit(OpCodes.Ret);

        // Events
        foreach (var eventInfo in _virtualType.GetEvents(BindingFlags.Public | BindingFlags.Instance))
        {
            var eventName = eventInfo.Name;
            var eventType = eventInfo.EventHandlerType;

            var addMethod = DefineEventAccessor(eventInfo, eventName, true);
            var removeMethod = DefineEventAccessor(eventInfo, eventName, false);

            // Define the event
            if (eventType == null)
            {
                continue;
            }
            var newEvent = _typeBuilder.DefineEvent(eventName, EventAttributes.None, eventType);
            if (addMethod != null)
                newEvent.SetAddOnMethod(addMethod);
            if (removeMethod != null)
                newEvent.SetRemoveOnMethod(removeMethod);
        }

        // Implement methods
        foreach (var method in _virtualType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (method.IsSpecialName || method.DeclaringType == typeof(object) || !method.IsVirtual)
            {
                Console.WriteLine($"Skipping method: {method.Name}");
                continue;
            }
            var paramTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
            var mb = _typeBuilder.DefineMethod(method.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig, method.ReturnType, paramTypes);
            il = mb.GetILGenerator();
            EmitLoadManager(il);
            EmitCallProcessManagerMethod(il, method);
            il.Emit(OpCodes.Ret);
        }
        return _typeBuilder.CreateType()!;
    }

    private void EmitLoadManager(ILGenerator il)
    {
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _managerField);
    }

    private static void EmitCreateArgsArray(ILGenerator il, ParameterInfo[] parameters)
    {
        if (parameters.Length > 0)
        {
            il.Emit(OpCodes.Ldc_I4, parameters.Length);
            il.Emit(OpCodes.Newarr, typeof(object));
            for (int i = 0; i < parameters.Length; i++)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldarg, i + 1);
                if (parameters[i].ParameterType.IsValueType)
                {
                    il.Emit(OpCodes.Box, parameters[i].ParameterType);
                }
                il.Emit(OpCodes.Stelem_Ref);
            }
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
    }

    private void EmitCallProcessManagerMethod(ILGenerator il, MethodInfo method)
    {
        Console.WriteLine($"Generating call for method: {method.Name}");
        if (method.Name == nameof(IBackgroundService.StopAsync))
        {
            var stopMethod = GetProcessManagerMethod(nameof(ProcessManager.GracefulShutdownAsync));
            il.Emit(OpCodes.Callvirt, stopMethod);
            return;
        }

        il.Emit(OpCodes.Ldstr, method.Name);
        EmitCreateArgsArray(il, method.GetParameters());
        if (method.ReturnType == typeof(Task))
        {
            var callMethodTask = GetProcessManagerMethod(nameof(ProcessManager.CallMethodAsync));
            il.Emit(OpCodes.Callvirt, callMethodTask);
        }
        else if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var t = method.ReturnType.GetGenericArguments()[0];
            var callMethodTaskT = GetProcessManagerMethod(nameof(ProcessManager.CallMethodGenericAsync)).MakeGenericMethod(t);
            il.Emit(OpCodes.Callvirt, callMethodTaskT);
        }
        else
        {
            var callSyncMethod = method.ReturnType == typeof(void)
              ? GetProcessManagerMethod(nameof(ProcessManager.CallMethod))
              : GetProcessManagerMethod(nameof(ProcessManager.CallMethodGeneric)).MakeGenericMethod(method.ReturnType);
            il.Emit(OpCodes.Callvirt, callSyncMethod);
        }
    }

    private MethodBuilder? DefineEventAccessor(EventInfo eventInfo, string eventName, bool isAdd)
    {
        var accessor = isAdd ? eventInfo.AddMethod : eventInfo.RemoveMethod;
        if (accessor == null)
        {
            throw new InvalidOperationException($"Accessor method for event {eventName} not found on interface {_virtualType.FullName}");
        }
        if (eventInfo.EventHandlerType == null)
        {
            throw new InvalidOperationException($"Event {eventName} not found on interface {_virtualType.FullName}");
        }
        var method = _typeBuilder.DefineMethod(accessor.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.SpecialName, typeof(void), [eventInfo.EventHandlerType]);
        var il = method.GetILGenerator();
        GenerateEventAccessorIL(il, eventName, isAdd ? nameof(ProcessManager.RegisterEventHandler) : nameof(ProcessManager.RemoveEventHandler));
        return method;
    }

    private static MethodInfo GetProcessManagerMethod(string methodName, Type[]? parameterTypes = null)
    {
        MethodInfo method;
        var flags = BindingFlags.Public | BindingFlags.Instance;
        if (parameterTypes != null)
        {
            method = managerType.GetMethod(methodName, flags, null, parameterTypes, null)!;
        }
        else
        {
            method = managerType.GetMethod(methodName, flags)!;
        }
        return method;
    }

    private void GenerateEventAccessorIL(ILGenerator il, string eventName, string accessorMethodName)
    {
        EmitLoadManager(il);
        il.Emit(OpCodes.Ldstr, eventName);
        il.Emit(OpCodes.Ldarg_1);
        var accessorMethod = GetProcessManagerMethod(accessorMethodName, [typeof(string), typeof(Delegate)]);
        il.Emit(OpCodes.Callvirt, accessorMethod);
        il.Emit(OpCodes.Ret);
    }
}