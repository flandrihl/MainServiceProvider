﻿using CommonServiceLocator;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace MainServiceProvider
{
    public class MainServiceProvider : IServiceLocator
    {
        private readonly Dictionary<Type, ConstructorInfo> _constructorInfos = new Dictionary<Type, ConstructorInfo>();
        private readonly string _currentGuid = Guid.NewGuid().ToString();
        private readonly Dictionary<Type, Dictionary<string, Delegate>> _factories = new Dictionary<Type, Dictionary<string, Delegate>>();
        private readonly Dictionary<Type, Dictionary<string, object>> _instancesRegistry = new Dictionary<Type, Dictionary<string, object>>();
        private readonly Dictionary<Type, Type> _interfaceToClassMap = new Dictionary<Type, Type>();
        private readonly object _syncLock = new object();
        private static readonly object _instanceLock = new object();

        private static MainServiceProvider _current;
        /// <summary>
        /// Gets the current instance.
        /// </summary>
        /// <value>
        /// The current instance.
        /// </value>
        public static MainServiceProvider Current
        {
            get
            {
                if (_current == null)
                    lock (_instanceLock)
                    {
                        if (_current == null)
                            _current = new MainServiceProvider();
                    }

                return _current;
            }
        }

        /// <summary>
        /// Checks whether at least one instance of a given class is already created in the container.
        /// </summary>
        /// <typeparam name="TClass">The class that is queried.</typeparam>
        /// <returns>True if at least on instance of the class is already created, false otherwise.</returns>
        public bool ContainsCreated<TClass>() => ContainsCreated<TClass>(null);

        /// <summary>
        /// Checks whether the instance with the given key is already created for a given class
        /// in the container.
        /// </summary>
        /// <typeparam name="TClass">The class that is queried.</typeparam>
        /// <param name="key">The key that is queried.</param>
        /// <returns>True if the instance with the given key is already registered for the given class,
        /// false otherwise.</returns>
        public bool ContainsCreated<TClass>(string key)
        {
            var classType = typeof(TClass);

            if (!_instancesRegistry.ContainsKey(classType))
                return false;

            if (string.IsNullOrEmpty(key))
                return _instancesRegistry[classType].Count > 0;

            return _instancesRegistry[classType].ContainsKey(key);
        }

        /// <summary>
        /// Gets a value indicating whether a given type T is already registered.
        /// </summary>
        /// <typeparam name="T">The type that the method checks for.</typeparam>
        /// <returns>True if the type is registered, false otherwise.</returns>
        public bool IsRegistered<T>()
        {
            var classType = typeof(T);
            return _interfaceToClassMap.ContainsKey(classType);
        }

        /// <summary>
        /// Gets a value indicating whether a given type T and a give key
        /// are already registered.
        /// </summary>
        /// <typeparam name="T">The type that the method checks for.</typeparam>
        /// <param name="key">The key that the method checks for.</param>
        /// <returns>True if the type and key are registered, false otherwise.</returns>
        public bool IsRegistered<T>(string key)
        {
            var classType = typeof(T);

            if (!_interfaceToClassMap.ContainsKey(classType)
                || !_factories.ContainsKey(classType))
                return false;

            return _factories[classType].ContainsKey(key);
        }

        /// <summary>
        /// Registers a given type for a given interface.
        /// </summary>
        /// <typeparam name="TInterface">The interface for which instances will be resolved.</typeparam>
        /// <typeparam name="TClass">The type that must be used to create instances.</typeparam>
        [SuppressMessage(
            "Microsoft.Design",
            "CA1004",
            Justification = "This syntax is better than the alternatives.")]
        public void Register<TInterface, TClass>()
            where TInterface : class
            where TClass : class, TInterface => Register<TInterface, TClass>(false);

        /// <summary>
        /// Registers a given type for a given interface with the possibility for immediate
        /// creation of the instance.
        /// </summary>
        /// <typeparam name="TInterface">The interface for which instances will be resolved.</typeparam>
        /// <typeparam name="TClass">The type that must be used to create instances.</typeparam>
        /// <param name="createInstanceImmediately">If true, forces the creation of the default
        /// instance of the provided class.</param>
        [SuppressMessage(
            "Microsoft.Design",
            "CA1004",
            Justification = "This syntax is better than the alternatives.")]
        public void Register<TInterface, TClass>(bool createInstanceImmediately)
            where TInterface : class
            where TClass : class, TInterface
        {
            lock (_syncLock)
            {
                var interfaceType = typeof(TInterface);
                var classType = typeof(TClass);

                if (_interfaceToClassMap.ContainsKey(interfaceType))
                {
                    if (_interfaceToClassMap[interfaceType] != classType)
                    {
                        throw new InvalidOperationException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "There is already a class registered for {0}.",
                                interfaceType.FullName));
                    }
                }
                else
                {
                    _interfaceToClassMap.Add(interfaceType, classType);
                    _constructorInfos.Add(classType, GetConstructorInfo(classType));
                }

                Func<TInterface> factory = MakeInstance<TInterface>;
                DoRegister(interfaceType, factory, _currentGuid);

                if (createInstanceImmediately)
                    GetInstance<TInterface>();
            }
        }

        /// <summary>
        /// Registers a given type.
        /// </summary>
        /// <typeparam name="TClass">The type that must be used to create instances.</typeparam>
        [SuppressMessage(
            "Microsoft.Design",
            "CA1004",
            Justification = "This syntax is better than the alternatives.")]
        public void Register<TClass>()
            where TClass : class => Register<TClass>(false);

        /// <summary>
        /// Registers a given type with the possibility for immediate
        /// creation of the instance.
        /// </summary>
        /// <typeparam name="TClass">The type that must be used to create instances.</typeparam>
        /// <param name="createInstanceImmediately">If true, forces the creation of the default
        /// instance of the provided class.</param>
        [SuppressMessage(
            "Microsoft.Design",
            "CA1004",
            Justification = "This syntax is better than the alternatives.")]
        public void Register<TClass>(bool createInstanceImmediately)
            where TClass : class
        {
            var classType = typeof(TClass);
            if (classType.IsInterface)
                throw new ArgumentException("An interface cannot be registered alone.");

            lock (_syncLock)
            {
                if (_factories.ContainsKey(classType)
                    && _factories[classType].ContainsKey(_currentGuid))
                {
                    if (!_constructorInfos.ContainsKey(classType))
                    {
                        // Throw only if constructorinfos have not been
                        // registered, which means there is a default factory
                        // for this class.
                        throw new InvalidOperationException(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Class {0} is already registered.",
                                classType));
                    }

                    return;
                }

                if (!_interfaceToClassMap.ContainsKey(classType))
                    _interfaceToClassMap.Add(classType, null);

                _constructorInfos.Add(classType, GetConstructorInfo(classType));
                Func<TClass> factory = MakeInstance<TClass>;
                DoRegister(classType, factory, _currentGuid);

                if (createInstanceImmediately)
                    GetInstance<TClass>();
            }
        }

        /// <summary>
        /// Registers a given instance for a given type.
        /// </summary>
        /// <typeparam name="TClass">The type that is being registered.</typeparam>
        /// <param name="factory">The factory method able to create the instance that
        /// must be returned when the given type is resolved.</param>
        public void Register<TClass>(Func<TClass> factory)
            where TClass : class => Register(factory, false);

        /// <summary>
        /// Registers a given instance for a given type with the possibility for immediate
        /// creation of the instance.
        /// </summary>
        /// <typeparam name="TClass">The type that is being registered.</typeparam>
        /// <param name="factory">The factory method able to create the instance that
        /// must be returned when the given type is resolved.</param>
        /// <param name="createInstanceImmediately">If true, forces the creation of the default
        /// instance of the provided class.</param>
        public void Register<TClass>(Func<TClass> factory, bool createInstanceImmediately)
            where TClass : class
        {
            if (factory == null)
                throw new ArgumentNullException("factory");

            lock (_syncLock)
            {
                var classType = typeof(TClass);

                if (_factories.ContainsKey(classType)
                    && _factories[classType].ContainsKey(_currentGuid))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "There is already a factory registered for {0}.",
                            classType.FullName));
                }

                if (!_interfaceToClassMap.ContainsKey(classType))
                    _interfaceToClassMap.Add(classType, null);

                DoRegister(classType, factory, _currentGuid);

                if (createInstanceImmediately)
                    GetInstance<TClass>();
            }
        }

        /// <summary>
        /// Registers a given instance for a given type and a given key.
        /// </summary>
        /// <typeparam name="TClass">The type that is being registered.</typeparam>
        /// <param name="factory">The factory method able to create the instance that
        /// must be returned when the given type is resolved.</param>
        /// <param name="key">The key for which the given instance is registered.</param>
        public void Register<TClass>(Func<TClass> factory, string key)
            where TClass : class => Register(factory, key, false);

        /// <summary>
        /// Registers a given instance for a given type and a given key with the possibility for immediate
        /// creation of the instance.
        /// </summary>
        /// <typeparam name="TClass">The type that is being registered.</typeparam>
        /// <param name="factory">The factory method able to create the instance that
        /// must be returned when the given type is resolved.</param>
        /// <param name="key">The key for which the given instance is registered.</param>
        /// <param name="createInstanceImmediately">If true, forces the creation of the default
        /// instance of the provided class.</param>
        public void Register<TClass>(
            Func<TClass> factory,
            string key,
            bool createInstanceImmediately)
            where TClass : class
        {

            if (factory == null)
                throw new ArgumentNullException("factory");

            lock (_syncLock)
            {
                var classType = typeof(TClass);

                if (_factories.ContainsKey(classType)
                    && _factories[classType].ContainsKey(key))
                {
                    throw new InvalidOperationException(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "There is already a factory registered for {0} with key {1}.",
                            classType.FullName,
                            key));
                }

                if (!_interfaceToClassMap.ContainsKey(classType))
                    _interfaceToClassMap.Add(classType, null);

                DoRegister(classType, factory, key);

                if (createInstanceImmediately)
                    GetInstance<TClass>(key);
            }
        }

        /// <summary>
        /// Resets the instance in its original states. This deletes all the
        /// registrations.
        /// </summary>
        public void Reset()
        {
            _interfaceToClassMap.Clear();
            _instancesRegistry.Clear();
            _constructorInfos.Clear();
            _factories.Clear();
        }

        /// <summary>
        /// Unregisters a class from the cache and removes all the previously
        /// created instances.
        /// </summary>
        /// <typeparam name="TClass">The class that must be removed.</typeparam>
        [SuppressMessage(
            "Microsoft.Design",
            "CA1004",
            Justification = "This syntax is better than the alternatives.")]
        public void Unregister<TClass>()
            where TClass : class
        {
            lock (_syncLock)
            {
                var serviceType = typeof(TClass);
                Type resolveTo;

                resolveTo = _interfaceToClassMap.ContainsKey(serviceType)
                    ? _interfaceToClassMap[serviceType] ?? serviceType
                    : serviceType;

                if (_instancesRegistry.ContainsKey(serviceType))
                    _instancesRegistry.Remove(serviceType);

                if (_interfaceToClassMap.ContainsKey(serviceType))
                    _interfaceToClassMap.Remove(serviceType);

                if (_factories.ContainsKey(serviceType))
                    _factories.Remove(serviceType);

                if (_constructorInfos.ContainsKey(resolveTo))
                    _constructorInfos.Remove(resolveTo);
            }
        }

        /// <summary>
        /// Removes the given instance from the cache. The class itself remains
        /// registered and can be used to create other instances.
        /// </summary>
        /// <typeparam name="TClass">The type of the instance to be removed.</typeparam>
        /// <param name="instance">The instance that must be removed.</param>
        public void Unregister<TClass>(TClass instance)
            where TClass : class
        {
            lock (_syncLock)
            {
                var classType = typeof(TClass);

                if (_instancesRegistry.ContainsKey(classType))
                {
                    var list = _instancesRegistry[classType];

                    var pairs = list.Where(pair => pair.Value == instance).ToList();
                    for (var index = 0; index < pairs.Count(); index++)
                    {
                        var key = pairs[index].Key;

                        list.Remove(key);
                    }
                }
            }
        }

        /// <summary>
        /// Removes the instance corresponding to the given key from the cache. The class itself remains
        /// registered and can be used to create other instances.
        /// </summary>
        /// <typeparam name="TClass">The type of the instance to be removed.</typeparam>
        /// <param name="key">The key corresponding to the instance that must be removed.</param>
        [SuppressMessage(
            "Microsoft.Design",
            "CA1004",
            Justification = "This syntax is better than the alternatives.")]
        public void Unregister<TClass>(string key)
            where TClass : class
        {
            lock (_syncLock)
            {
                var classType = typeof(TClass);

                if (_instancesRegistry.ContainsKey(classType))
                {
                    var list = _instancesRegistry[classType];

                    var pairs = list.Where(pair => pair.Key == key).ToList();
                    for (var index = 0; index < pairs.Count(); index++)
                    {
                        list.Remove(pairs[index].Key);
                    }
                }

                if (_factories.ContainsKey(classType) && _factories[classType].ContainsKey(key))
                    _factories[classType].Remove(key);
            }
        }

        private object DoGetService(Type serviceType, string key, bool cache = true)
        {
            lock (_syncLock)
            {
                if (string.IsNullOrEmpty(key))
                    key = _currentGuid;

                Dictionary<string, object> instances = null;

                if (!_instancesRegistry.ContainsKey(serviceType))
                {
                    if (!_interfaceToClassMap.ContainsKey(serviceType))
                    {
#if NETSTANDARD1_0
                        throw new InvalidOperationException(
#else
                        throw new ActivationException(
#endif
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "Type not found in cache: {0}.",
                                serviceType.FullName));
                    }

                    if (cache)
                    {
                        instances = new Dictionary<string, object>();
                        _instancesRegistry.Add(serviceType, instances);
                    }
                }
                else
                {
                    instances = _instancesRegistry[serviceType];
                }

                if (instances != null
                    && instances.ContainsKey(key))
                    return instances[key];

                object instance = null;

                if (_factories.ContainsKey(serviceType))
                {
                    if (_factories[serviceType].ContainsKey(key))
                        instance = _factories[serviceType][key].DynamicInvoke(null);
                    else
                    {
                        if (_factories[serviceType].ContainsKey(_currentGuid))
                            instance = _factories[serviceType][_currentGuid].DynamicInvoke(null);
                        else
                        {
#if NETSTANDARD1_0
                            throw new InvalidOperationException(
#else
                            throw new ActivationException(
#endif
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "Type not found in cache without a key: {0}",
                                    serviceType.FullName));
                        }
                    }
                }

                if (cache && instances != null)
                    instances.Add(key, instance);

                return instance;
            }
        }

        private void DoRegister<TClass>(Type classType, Func<TClass> factory, string key)
        {
            if (_factories.ContainsKey(classType))
            {
                if (_factories[classType].ContainsKey(key))
                {
                    // The class is already registered, ignore and continue.
                    return;
                }

                _factories[classType].Add(key, factory);
            }
            else
            {
                var list = new Dictionary<string, Delegate>
                {
                    { key, factory }
                };

                _factories.Add(classType, list);
            }
        }

        private ConstructorInfo GetConstructorInfo(Type serviceType)
        {
            Type resolveTo;

            resolveTo = _interfaceToClassMap.ContainsKey(serviceType)
                ? _interfaceToClassMap[serviceType] ?? serviceType
                : serviceType;

            var constructorInfos = resolveTo.GetConstructors();

            if (constructorInfos.Length > 1)
            {
                if (constructorInfos.Length > 2)
                    return GetPreferredConstructorInfo(constructorInfos, resolveTo);

                if (constructorInfos.FirstOrDefault(i => i.Name == ".cctor") == null)
                    return GetPreferredConstructorInfo(constructorInfos, resolveTo);

                var first = constructorInfos.FirstOrDefault(i => i.Name != ".cctor");

                if (first == null
                    || !first.IsPublic)
                {
#if NETSTANDARD1_0
                    throw new InvalidOperationException(
#else
                    throw new ActivationException(
#endif
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "Cannot register: No public constructor found in {0}.",
                            resolveTo.Name));
                }

                return first;
            }

            if (constructorInfos.Length == 0
                || (constructorInfos.Length == 1
                    && !constructorInfos[0].IsPublic))
            {
#if NETSTANDARD1_0
                throw new InvalidOperationException(
#else
                throw new ActivationException(
#endif
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Cannot register: No public constructor found in {0}.",
                        resolveTo.Name));
            }

            return constructorInfos[0];
        }

        [SuppressMessage(
            "Microsoft.Naming",
            "CA2204:Literals should be spelled correctly",
            MessageId = "PreferredConstructor")]
        private static ConstructorInfo GetPreferredConstructorInfo(IEnumerable<ConstructorInfo> constructorInfos, Type resolveTo)
        {
            var preferredConstructorInfo
                = (from t in constructorInfos
                   let attribute = Attribute.GetCustomAttribute(t, typeof(MainConstructorAttribute))
                   where attribute != null
                   select t).FirstOrDefault();

            if (preferredConstructorInfo == null)
            {
#if NETSTANDARD1_0
                throw new InvalidOperationException(
#else
                throw new ActivationException(
#endif
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "Cannot register: Multiple constructors found in {0} but none marked with PreferredConstructor.",
                        resolveTo.Name));
            }

            return preferredConstructorInfo;
        }

        private TClass MakeInstance<TClass>()
        {
            var serviceType = typeof(TClass);

            var constructor = _constructorInfos.ContainsKey(serviceType)
                                  ? _constructorInfos[serviceType]
                                  : GetConstructorInfo(serviceType);

            var parameterInfos = constructor.GetParameters();

            if (parameterInfos.Length == 0)
                return (TClass)constructor.Invoke(Array.Empty<object>());

            var parameters = new object[parameterInfos.Length];

            foreach (var parameterInfo in parameterInfos)
            {
                parameters[parameterInfo.Position] = GetService(parameterInfo.ParameterType);
            }

            return (TClass)constructor.Invoke(parameters);
        }

        /// <summary>
        /// Provides a way to get all the created instances of a given type available in the
        /// cache. Registering a class or a factory does not automatically
        /// create the corresponding instance! To create an instance, either register
        /// the class or the factory with createInstanceImmediately set to true,
        /// or call the GetInstance method before calling GetAllCreatedInstances.
        /// Alternatively, use the GetAllInstances method, which auto-creates default
        /// instances for all registered classes.
        /// </summary>
        /// <param name="serviceType">The class of which all instances
        /// must be returned.</param>
        /// <returns>All the already created instances of the given type.</returns>
        public IEnumerable<object> GetAllCreatedInstances(Type serviceType)
        {
            if (_instancesRegistry.ContainsKey(serviceType))
                return _instancesRegistry[serviceType].Values;

            return new List<object>();
        }

        /// <summary>
        /// Provides a way to get all the created instances of a given type available in the
        /// cache. Registering a class or a factory does not automatically
        /// create the corresponding instance! To create an instance, either register
        /// the class or the factory with createInstanceImmediately set to true,
        /// or call the GetInstance method before calling GetAllCreatedInstances.
        /// Alternatively, use the GetAllInstances method, which auto-creates default
        /// instances for all registered classes.
        /// </summary>
        /// <typeparam name="TService">The class of which all instances
        /// must be returned.</typeparam>
        /// <returns>All the already created instances of the given type.</returns>
        public IEnumerable<TService> GetAllCreatedInstances<TService>()
        {
            var serviceType = typeof(TService);
            return GetAllCreatedInstances(serviceType)
                .Select(instance => (TService)instance);
        }

        #region Implementation of IServiceProvider

#if NETSTANDARD1_0
        /// <summary>
        /// Gets the service object of the specified type.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <returns>
        /// A service object of type <paramref name="serviceType" />.
        /// </returns>
        /// <param name="serviceType">An object that specifies the type of service object to get.</param>
#else
        /// <summary>
        /// Gets the service object of the specified type.
        /// </summary>
        /// <exception cref="ActivationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <returns>
        /// A service object of type <paramref name="serviceType" />.
        /// </returns>
        /// <param name="serviceType">An object that specifies the type of service object to get.</param>
#endif
        public object GetService(Type serviceType) =>
            DoGetService(serviceType, _currentGuid);

        #endregion

        #region Implementation of IServiceLocator

        /// <summary>
        /// Provides a way to get all the created instances of a given type available in the
        /// cache. Calling this method auto-creates default
        /// instances for all registered classes.
        /// </summary>
        /// <param name="serviceType">The class of which all instances
        /// must be returned.</param>
        /// <returns>All the instances of the given type.</returns>
        public IEnumerable<object> GetAllInstances(Type serviceType)
        {
            lock (_factories)
            {
                if (_factories.ContainsKey(serviceType))
                {
                    foreach (var factory in _factories[serviceType])
                    {
                        GetInstance(serviceType, factory.Key);
                    }
                }
            }

            if (_instancesRegistry.ContainsKey(serviceType))
                return _instancesRegistry[serviceType].Values;


            return new List<object>();
        }

        /// <summary>
        /// Provides a way to get all the created instances of a given type available in the
        /// cache. Calling this method auto-creates default
        /// instances for all registered classes.
        /// </summary>
        /// <typeparam name="TService">The class of which all instances
        /// must be returned.</typeparam>
        /// <returns>All the instances of the given type.</returns>
        public IEnumerable<TService> GetAllInstances<TService>()
        {
            var serviceType = typeof(TService);
            return GetAllInstances(serviceType)
                .Select(instance => (TService)instance);
        }

#if NETSTANDARD1_0
        /// <summary>
        /// Provides a way to get an instance of a given type. If no instance had been instantiated 
        /// before, a new instance will be created. If an instance had already
        /// been created, that same instance will be returned.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <param name="serviceType">The class of which an instance
        /// must be returned.</param>
        /// <returns>An instance of the given type.</returns>
#else
        /// <summary>
        /// Provides a way to get an instance of a given type. If no instance had been instantiated 
        /// before, a new instance will be created. If an instance had already
        /// been created, that same instance will be returned.
        /// </summary>
        /// <exception cref="ActivationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <param name="serviceType">The class of which an instance
        /// must be returned.</param>
        /// <returns>An instance of the given type.</returns>
#endif
        public object GetInstance(Type serviceType) =>
            DoGetService(serviceType, _currentGuid);

#if NETSTANDARD1_0
        /// <summary>
        /// Provides a way to get an instance of a given type. This method
        /// always returns a new instance and doesn't cache it in the IOC container.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <param name="serviceType">The class of which an instance
        /// must be returned.</param>
        /// <returns>An instance of the given type.</returns>
#else
        /// <summary>
        /// Provides a way to get an instance of a given type. This method
        /// always returns a new instance and doesn't cache it in the IOC container.
        /// </summary>
        /// <exception cref="ActivationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <param name="serviceType">The class of which an instance
        /// must be returned.</param>
        /// <returns>An instance of the given type.</returns>
#endif
        public object GetInstanceWithoutCaching(Type serviceType) =>
            DoGetService(serviceType, _currentGuid, false);

#if NETSTANDARD1_0
        /// <summary>
        /// Provides a way to get an instance of a given type corresponding
        /// to a given key. If no instance had been instantiated with this
        /// key before, a new instance will be created. If an instance had already
        /// been created with the same key, that same instance will be returned.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <param name="serviceType">The class of which an instance must be returned.</param>
        /// <param name="key">The key uniquely identifying this instance.</param>
        /// <returns>An instance corresponding to the given type and key.</returns>
#else
        /// <summary>
        /// Provides a way to get an instance of a given type corresponding
        /// to a given key. If no instance had been instantiated with this
        /// key before, a new instance will be created. If an instance had already
        /// been created with the same key, that same instance will be returned.
        /// </summary>
        /// <exception cref="ActivationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <param name="serviceType">The class of which an instance must be returned.</param>
        /// <param name="key">The key uniquely identifying this instance.</param>
        /// <returns>An instance corresponding to the given type and key.</returns>
#endif
        public object GetInstance(Type serviceType, string key) =>
            DoGetService(serviceType, key);

#if NETSTANDARD1_0
        /// <summary>
        /// Provides a way to get an instance of a given type. This method
        /// always returns a new instance and doesn't cache it in the IOC container.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <param name="serviceType">The class of which an instance must be returned.</param>
        /// <param name="key">The key uniquely identifying this instance.</param>
        /// <returns>An instance corresponding to the given type and key.</returns>
#else
        /// <summary>
        /// Provides a way to get an instance of a given type. This method
        /// always returns a new instance and doesn't cache it in the IOC container.
        /// </summary>
        /// <exception cref="ActivationException">If the type serviceType has not
        /// been registered before calling this method.</exception>
        /// <param name="serviceType">The class of which an instance must be returned.</param>
        /// <param name="key">The key uniquely identifying this instance.</param>
        /// <returns>An instance corresponding to the given type and key.</returns>
#endif
        public object GetInstanceWithoutCaching(Type serviceType, string key) =>
            DoGetService(serviceType, key, false);

#if NETSTANDARD1_0
        /// <summary>
        /// Provides a way to get an instance of a given type. If no instance had been instantiated 
        /// before, a new instance will be created. If an instance had already
        /// been created, that same instance will be returned.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type TService has not
        /// been registered before calling this method.</exception>
        /// <typeparam name="TService">The class of which an instance
        /// must be returned.</typeparam>
        /// <returns>An instance of the given type.</returns>
#else
        /// <summary>
        /// Provides a way to get an instance of a given type. If no instance had been instantiated 
        /// before, a new instance will be created. If an instance had already
        /// been created, that same instance will be returned.
        /// </summary>
        /// <exception cref="ActivationException">If the type TService has not
        /// been registered before calling this method.</exception>
        /// <typeparam name="TService">The class of which an instance
        /// must be returned.</typeparam>
        /// <returns>An instance of the given type.</returns>
#endif
        public TService GetInstance<TService>() =>
            (TService)DoGetService(typeof(TService), _currentGuid);

#if NETSTANDARD1_0
        /// <summary>
        /// Provides a way to get an instance of a given type. This method
        /// always returns a new instance and doesn't cache it in the IOC container.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type TService has not
        /// been registered before calling this method.</exception>
        /// <typeparam name="TService">The class of which an instance
        /// must be returned.</typeparam>
        /// <returns>An instance of the given type.</returns>
#else
        /// <summary>
        /// Provides a way to get an instance of a given type. This method
        /// always returns a new instance and doesn't cache it in the IOC container.
        /// </summary>
        /// <exception cref="ActivationException">If the type TService has not
        /// been registered before calling this method.</exception>
        /// <typeparam name="TService">The class of which an instance
        /// must be returned.</typeparam>
        /// <returns>An instance of the given type.</returns>
#endif
        public TService GetInstanceWithoutCaching<TService>() =>
            (TService)DoGetService(typeof(TService), _currentGuid, false);

#if NETSTANDARD1_0
        /// <summary>
        /// Provides a way to get an instance of a given type corresponding
        /// to a given key. If no instance had been instantiated with this
        /// key before, a new instance will be created. If an instance had already
        /// been created with the same key, that same instance will be returned.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type TService has not
        /// been registered before calling this method.</exception>
        /// <typeparam name="TService">The class of which an instance must be returned.</typeparam>
        /// <param name="key">The key uniquely identifying this instance.</param>
        /// <returns>An instance corresponding to the given type and key.</returns>
#else
        /// <summary>
        /// Provides a way to get an instance of a given type corresponding
        /// to a given key. If no instance had been instantiated with this
        /// key before, a new instance will be created. If an instance had already
        /// been created with the same key, that same instance will be returned.
        /// </summary>
        /// <exception cref="ActivationException">If the type TService has not
        /// been registered before calling this method.</exception>
        /// <typeparam name="TService">The class of which an instance must be returned.</typeparam>
        /// <param name="key">The key uniquely identifying this instance.</param>
        /// <returns>An instance corresponding to the given type and key.</returns>
#endif
        public TService GetInstance<TService>(string key) =>
            (TService)DoGetService(typeof(TService), key);

#if NETSTANDARD1_0
        /// <summary>
        /// Provides a way to get an instance of a given type. This method
        /// always returns a new instance and doesn't cache it in the IOC container.
        /// </summary>
        /// <exception cref="InvalidOperationException">If the type TService has not
        /// been registered before calling this method.</exception>
        /// <typeparam name="TService">The class of which an instance must be returned.</typeparam>
        /// <param name="key">The key uniquely identifying this instance.</param>
        /// <returns>An instance corresponding to the given type and key.</returns>
#else
        /// <summary>
        /// Provides a way to get an instance of a given type. This method
        /// always returns a new instance and doesn't cache it in the IOC container.
        /// </summary>
        /// <exception cref="ActivationException">If the type TService has not
        /// been registered before calling this method.</exception>
        /// <typeparam name="TService">The class of which an instance must be returned.</typeparam>
        /// <param name="key">The key uniquely identifying this instance.</param>
        /// <returns>An instance corresponding to the given type and key.</returns>
#endif
        public TService GetInstanceWithoutCaching<TService>(string key) =>
            (TService)DoGetService(typeof(TService), key, false);

        #endregion
    }
}