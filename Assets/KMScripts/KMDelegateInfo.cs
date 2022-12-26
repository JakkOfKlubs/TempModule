using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

[HelpURL("https://github.com/Qkrisi/ktanemodkit/wiki/KMDelegateInfo")]
[AddComponentMenu("")]
public sealed class KMDelegateInfo : MonoBehaviour, ISerializationCallbackReceiver
{
    [Serializable]
    public class DelegateInfo
    {
        public GameObject SourceGameObject;
        public Component SourceComponent;
        public string MethodName;

        public Component DestinationComponent;
        public string DelegateName;
         
        public int Index;
        public bool UseReturnValue = true;
    }

    public const BindingFlags FLAGS = BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy;
    public const string SpecialPostfix = "*";
    
    public List<DelegateInfo> DelegateInfos = new List<DelegateInfo>();
    [HideInInspector] public bool AllowUnityComponents;
    [HideInInspector] public bool AllowInheriteds = true;
    [HideInInspector] public bool AllowCompilerGenerateds;
    
    #region Serialization
    //Deal with Unity not liking System.Serializable
    [SerializeField, HideInInspector] private List<GameObject> SourceGameObjects = new List<GameObject>();
    [SerializeField, HideInInspector] private List<Component> SourceComponents = new List<Component>();
    [SerializeField, HideInInspector] private List<string> MethodNames = new List<string>();
    [SerializeField, HideInInspector] private List<Component> DestinationComponents = new List<Component>();
    [SerializeField, HideInInspector] private List<string> DelegateNames = new List<string>();
    [SerializeField, HideInInspector] private List<int> Indexes = new List<int>();
    [SerializeField, HideInInspector] private List<bool> UseReturnValues = new List<bool>();
    
    public void OnAfterDeserialize()
    {
        #if !UNITY_EDITOR   //We only need to recreate the DelegateInfo objects in the game
        DelegateInfos.Clear();
        var i = -1;
        while (++i > -1)
        {
            try
            {
                DelegateInfos.Add(new DelegateInfo
                {
                    SourceGameObject = SourceGameObjects[i],
                    SourceComponent = SourceComponents[i],
                    MethodName = MethodNames[i],
                    DestinationComponent = DestinationComponents[i],
                    DelegateName = DelegateNames[i],
                    Index = Indexes[i],
                    UseReturnValue = UseReturnValues[i]
                });
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }
        }

        //Some memory optimization
        SourceGameObjects.Clear();
        SourceComponents.Clear();
        MethodNames.Clear();
        DestinationComponents.Clear();
        DelegateNames.Clear();
        Indexes.Clear();
        UseReturnValues.Clear();
        #endif
    }

    public void OnBeforeSerialize()
    {
        SourceGameObjects.Clear();
        SourceComponents.Clear();
        MethodNames.Clear();
        DestinationComponents.Clear();
        DelegateNames.Clear();
        Indexes.Clear();
        UseReturnValues.Clear();
        foreach (var delegateInfo in DelegateInfos)
        {
            SourceGameObjects.Add(delegateInfo.SourceGameObject);
            SourceComponents.Add(delegateInfo.SourceComponent);
            MethodNames.Add(delegateInfo.MethodName);
            DestinationComponents.Add(delegateInfo.DestinationComponent);
            DelegateNames.Add(delegateInfo.DelegateName);
            Indexes.Add(delegateInfo.Index);
            UseReturnValues.Add(delegateInfo.UseReturnValue);
        }
    }

    #endregion
    
    private static ModuleBuilder moduleBuilder;
    private static Dictionary<Type, Type> DynamicDelegateTypes;
    private static MethodInfo MethodInfoInvokeMethod;

    //Create a proxy class/method for the delegates capturing the caller instance
    private Type CreateDynamicDelegateType(Type DelegateType, MethodInfo InvokeMethod)
    {
        if (DynamicDelegateTypes.ContainsKey(DelegateType))
            return DynamicDelegateTypes[DelegateType];
        if (moduleBuilder == null && !AssemblyShare.TryGetValue("KMDelegateProxies_moduleBuilder", out moduleBuilder))
        {
            moduleBuilder = AppDomain.CurrentDomain
                .DefineDynamicAssembly(new AssemblyName("KMDelegateProxies"), AssemblyBuilderAccess.RunAndSave)
                .DefineDynamicModule("HandlerModule");
            AssemblyShare.Add("KMDelegateProxies_moduleBuilder", moduleBuilder);
        }

        var typeBuilder = moduleBuilder.DefineType(DelegateType.Name + "Proxy", 
            TypeAttributes.Public |
            TypeAttributes.Class | 
            TypeAttributes.AutoClass |
            TypeAttributes.AnsiClass |
            TypeAttributes.BeforeFieldInit |
            TypeAttributes.AutoLayout, null);

        var returnType = InvokeMethod.ReturnType;
        var invokeParameters = InvokeMethod.GetParameters();
        //Declare fields and method
        var componentFieldBuilder = typeBuilder.DefineField("__component", typeof(Component), FieldAttributes.Public);
        var sourceComponentFieldBuilder = typeBuilder.DefineField("__sourceComponent", typeof(Component), FieldAttributes.Public);
        var sourceMethodInfoFieldBuilder = typeBuilder.DefineField("__sourceMethodInfo", typeof(MethodInfo), FieldAttributes.Public);
        var delegateProxyBuilder = typeBuilder.DefineMethod("Invoke",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            returnType, invokeParameters.Select(p => p.ParameterType).ToArray());
        //
        var ilGenerator = delegateProxyBuilder.GetILGenerator();
        
        //Create array for parametes
        var argsBuilder = ilGenerator.DeclareLocal(typeof(object[]));
        ilGenerator.Emit(OpCodes.Ldc_I4, invokeParameters.Length + 1);  //Proxy parameters + caller
        ilGenerator.Emit(OpCodes.Newarr, typeof(object));
        ilGenerator.Emit(OpCodes.Stloc, argsBuilder);
        //
        
        //Load parameters into the array
        ilGenerator.Emit(OpCodes.Ldloc, argsBuilder);
        
        //Add the caller component into the array ([0])
        ilGenerator.Emit(OpCodes.Ldc_I4_0); //Index
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Ldfld, componentFieldBuilder);
        ilGenerator.Emit(OpCodes.Stelem_Ref);
        //
        
        //Add the proxied parameters into the array ([i])
        for (int i = 1; i <= invokeParameters.Length; i++)
        {
            ilGenerator.Emit(OpCodes.Ldloc, argsBuilder);
            ilGenerator.Emit(OpCodes.Ldc_I4, i);    //Index
            ilGenerator.Emit(OpCodes.Ldarg, i);
            var parameterType = invokeParameters[i - 1].ParameterType;
            
            if(parameterType.IsValueType)   //Value types should be boxed into object in order to be able to add them to an object array, castclass for reference types isn't necessary
                ilGenerator.Emit(OpCodes.Box, parameterType);
            
            ilGenerator.Emit(OpCodes.Stelem_Ref);
        }
        //
        
        //Load the parameters for MethodInfo.Invoke
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Ldfld, sourceMethodInfoFieldBuilder);  //Object reference (the MethodInfo to execute it on)
        ilGenerator.Emit(OpCodes.Ldarg_0);
        ilGenerator.Emit(OpCodes.Ldfld, sourceComponentFieldBuilder);   //The object reference of the method itself
        ilGenerator.Emit(OpCodes.Ldloc, argsBuilder);                   //Parameters
        //
        
        //Call MethodInfo.Invoke and return the value
        ilGenerator.Emit(OpCodes.Callvirt, MethodInfoInvokeMethod);
        ilGenerator.Emit(returnType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, returnType);   //Cast/unbox the returned object to the type that needs to be returned
        ilGenerator.Emit(OpCodes.Ret);
        //
        
        typeBuilder.DefineDefaultConstructor(MethodAttributes.Public | MethodAttributes.SpecialName |
                                             MethodAttributes.RTSpecialName);
        var type = typeBuilder.CreateType();
        DynamicDelegateTypes.Add(DelegateType, type);
        return type;
    }


    private Delegate CreateDynamicDelegate(Type DelegateType, MethodInfo InvokeMethod, Component component, MethodInfo SourceMethodInfo, Component SourceComponent)
    {
        var proxyType = CreateDynamicDelegateType(DelegateType, InvokeMethod);
        var proxy = Activator.CreateInstance(proxyType);
        proxyType.GetField("__component", FLAGS).SetValue(proxy, component);
        proxyType.GetField("__sourceComponent", FLAGS).SetValue(proxy, SourceComponent);
        proxyType.GetField("__sourceMethodInfo", FLAGS).SetValue(proxy, SourceMethodInfo);
        return Delegate.CreateDelegate(DelegateType, proxy, proxyType.GetMethod("Invoke", FLAGS));
    }
    
    private void Awake()
    {
        if (DynamicDelegateTypes == null)
            DynamicDelegateTypes =
                AssemblyShare.GetOrAdd("KMDelegateProxies_DynamicDelegateTypes", new Dictionary<Type, Type>());
        if (DelegateInfos == null)
            return;
        if (MethodInfoInvokeMethod == null)
            MethodInfoInvokeMethod = typeof(MethodInfo).GetMethod("Invoke", FLAGS, Type.DefaultBinder,
                new[] { typeof(object), typeof(object[]) }, null);
        foreach (var delegateInfo in DelegateInfos.OrderBy(d => d.UseReturnValue ? 1 : 0))
        {
            if (delegateInfo.SourceGameObject == null || delegateInfo.SourceComponent == null ||
                delegateInfo.DestinationComponent == null ||
                string.IsNullOrEmpty(delegateInfo.MethodName) || string.IsNullOrEmpty(delegateInfo.DelegateName))
            {
                Debug.Log("[KMDelegateInfo] Incomplete delegate forward, skipping");
                continue;
            }
            var componentType = delegateInfo.DestinationComponent.GetType();
            var delegateField = componentType.GetField(delegateInfo.DelegateName, FLAGS);
            if (delegateField == null)
            {
                Debug.LogFormat("[KMDelegateInfo] Destination delegate {0} could not be found", delegateInfo.DelegateName);
                continue;
            }
            var delegateType = delegateField.FieldType;
            if (!typeof(Delegate).IsAssignableFrom(delegateType))
            {
                Debug.LogFormat("[KMDelegateInfo] Specified destination delegate is not delegate type: {0}", delegateType.FullName);
                continue;
            }
            var delegateMethod = delegateType.GetMethod("Invoke", FLAGS);
            if (delegateMethod == null)     //This should not happen but just in case
            {
                Debug.Log("[KMDelegateInfo] No Invoke method found on delegate");
                continue;
            }
            var parameterTypes = delegateMethod.GetParameters().Select(p => p.ParameterType).ToList();
            var methodName = delegateInfo.MethodName;
            var createDynamicDelegate = false;
            if (methodName.EndsWith(SpecialPostfix))
            {
                methodName = methodName.Remove(methodName.Length - SpecialPostfix.Length);
                createDynamicDelegate = true;
                parameterTypes.Insert(0, componentType);
            }
            var sourceMethod = delegateInfo.SourceComponent.GetType().GetMethod(methodName, FLAGS,
                Type.DefaultBinder, parameterTypes.ToArray(), null);
            if (sourceMethod == null || !delegateMethod.ReturnType.IsAssignableFrom(sourceMethod.ReturnType))
            {
                Debug.LogFormat("[KMDelegateInfo] Delegate return type mismatch");
                continue;
            }
            var @delegate = createDynamicDelegate
                ? CreateDynamicDelegate(delegateType, delegateMethod, delegateInfo.DestinationComponent,
                    sourceMethod, delegateInfo.SourceComponent)
                : Delegate.CreateDelegate(delegateType, delegateInfo.SourceComponent, sourceMethod);
            
            if (delegateField.DeclaringType == typeof(KMSelectable) && delegateField.Name == "OnDefocus")
                @delegate = GameFixes.OnDefocus((Action)@delegate);
            
            delegateField.SetValue(delegateInfo.DestinationComponent,
                Delegate.Combine((Delegate)delegateField.GetValue(delegateInfo.DestinationComponent), @delegate));
        }
    }
}