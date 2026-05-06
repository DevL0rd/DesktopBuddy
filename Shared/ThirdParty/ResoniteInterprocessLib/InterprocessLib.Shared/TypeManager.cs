using Renderite.Shared;
using System.Reflection;

using System;
using System.Collections.Generic;

#nullable enable

namespace InterprocessLib;

internal class TypeManager
{
	private bool _initializedCoreTypes = false;

	private static readonly MethodInfo _registerTypeMethod = typeof(TypeManager).GetMethod(nameof(RegisterType), BindingFlags.NonPublic | BindingFlags.Instance) ?? throw new MissingMethodException(nameof(RegisterType));

	private readonly List<Type> _newTypes = new();

	private readonly List<Func<IMemoryPackable>> _borrowers = new();

	private readonly List<Action<IMemoryPackable>> _returners = new();

	private readonly Dictionary<Type, int> _typeToIndex = new();

	private IMemoryPackerEntityPool _pool;

	private static readonly MethodInfo _borrowMethod = typeof(TypeManager).GetMethod(nameof(Borrow), BindingFlags.Instance | BindingFlags.NonPublic, null, [], null) ?? throw new MissingMethodException(nameof(Borrow));
	private static readonly MethodInfo _returnMethod = typeof(TypeManager).GetMethod(nameof(Return), BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(IMemoryPackable)], null) ?? throw new MissingMethodException(nameof(Return));
	private static readonly List<Type> _coreTypes =
	[
		typeof(QueueOwnerInitCommand),
		typeof(TypeRegistrationCommand),
	];

	private Action<Type>? _onRegisteredCallback;

	internal TypeManager(IMemoryPackerEntityPool pool, Action<Type>? onRegisteredCallback)
	{
		_pool = pool;
		_onRegisteredCallback = onRegisteredCallback;
		InitializeCoreTypes();
	}

	internal void InitializeCoreTypes()
	{
		if (_initializedCoreTypes) return;

		_coreTypes.ForEach(InvokeRegisterType);

		_initializedCoreTypes = true;
	}

	internal Type GetTypeFromIndex(int index)
	{
		return _newTypes[index];
	}

	internal int GetTypeIndex(Type type)
	{
		return _typeToIndex[type];
	}

	internal bool IsTypeRegistered<T>() where T : class, IMemoryPackable, new()
	{
		return _typeToIndex.ContainsKey(typeof(T));
	}

	internal void InvokeRegisterType(Type type)
	{
		_registerTypeMethod!.MakeGenericMethod(type).Invoke(this, null);
	}

	internal void RegisterType<T>() where T : class, IMemoryPackable, new()
	{
		var type = typeof(T);

		_newTypes.Add(type);
		_borrowers.Add((Func<IMemoryPackable>)_borrowMethod!.MakeGenericMethod(type).CreateDelegate(typeof(Func<IMemoryPackable>), this));
		_returners.Add((Action<IMemoryPackable>)_returnMethod!.MakeGenericMethod(type).CreateDelegate(typeof(Action<IMemoryPackable>), this));
		_typeToIndex[type] = _newTypes.Count - 1;

		if (!_coreTypes.Contains(type))
			_onRegisteredCallback?.Invoke(type);
	}

	private IMemoryPackable Borrow<T>() where T : class, IMemoryPackable, new()
	{
		return _pool.Borrow<T>();
	}

	private void Return<T>(IMemoryPackable obj) where T : class, IMemoryPackable, new()
	{
		_pool.Return((T)obj);
	}

	internal IMemoryPackable Borrow(Type type)
	{
		return _borrowers[_typeToIndex[type]]();
	}

	internal void Return(Type type, IMemoryPackable obj)
	{
		_returners[_typeToIndex[type]](obj);
	}

	internal IMemoryPackable Borrow(int index)
	{
		return _borrowers[index]();
	}

	internal void Return(int index, IMemoryPackable obj)
	{
		_returners[index](obj);
	}
}
