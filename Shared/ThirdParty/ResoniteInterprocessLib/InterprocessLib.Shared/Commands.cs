using Renderite.Shared;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

#nullable enable

namespace InterprocessLib;

// IMPORTANT:
// RendererCommand derived classes MUST NOT have constructors because it breaks Unity for some reason

// internal abstract class IdentifiableCommand : IMemoryPackable
// {
// 	//public string? Owner;
// 	public string? Id;

// 	public virtual void Pack(ref MemoryPacker packer)
// 	{
// 		//packer.Write(Owner!);
// 		packer.Write(Id!);
// 	}

// 	public virtual void Unpack(ref MemoryUnpacker unpacker)
// 	{
// 		//unpacker.Read(ref Owner!);
// 		unpacker.Read(ref Id!);
// 	}

// 	public override string ToString()
// 	{
// 		return $"IdentifiableCommand:{Id}";
// 	}
// }

internal abstract class CollectionCommand : IMemoryPackable
{
	public abstract IEnumerable? UntypedCollection { get; }
	public abstract int? Length { get; }
	public abstract Type StoredType { get; }
	public abstract Type CollectionType { get; }
	public abstract void Pack(ref MemoryPacker packer);
	public abstract void Unpack(ref MemoryUnpacker unpacker);
    public override string ToString()
	{
		return $"CollectionCommand:{CollectionType.Name}<{StoredType.Name}>:Length={Length?.ToString() ?? "NULL"}";
	}
}

internal abstract class ValueCommand : IMemoryPackable
{
	public abstract object UntypedValue { get; }
	public abstract Type ValueType { get; }
	public abstract void Pack(ref MemoryPacker packer);
	public abstract void Unpack(ref MemoryUnpacker unpacker);
    public override string ToString()
	{
		return $"ValueCommand<{ValueType.Name}>:{UntypedValue}";
	}
}

internal abstract class ObjectCommand : IMemoryPackable
{
	public abstract object? UntypedObject { get; }
	public abstract Type ObjectType { get; }
	public abstract void Pack(ref MemoryPacker packer);
	public abstract void Unpack(ref MemoryUnpacker unpacker);
	public override string ToString()
	{
		return $"ObjectCommand<{ObjectType.Name}>:{UntypedObject?.ToString() ?? "NULL"}";
	}
}

internal sealed class EmptyCommand : IMemoryPackable
{
    public void Pack(ref MemoryPacker packer)
    {
    }
    public void Unpack(ref MemoryUnpacker unpacker)
    {
    }
	public override string ToString()
	{
		return $"EmptyCommand";
	}
}

internal sealed class ValueCollectionCommand<C, T> : CollectionCommand where C : ICollection<T>?, new() where T : unmanaged
{
	public C? Values;

    public override int? Length => Values?.Count;

	public override IEnumerable? UntypedCollection => Values;

	public override Type StoredType => typeof(T);

	public override Type CollectionType => typeof(C);

	public override void Pack(ref MemoryPacker packer)
	{
		var len = Values?.Count ?? -1;
		packer.Write(len);
		if (Values != null)
		{
			foreach (var value in Values)
			{
				packer.Write(value);
			}
		}
	}

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
		int len = 0;
		unpacker.Read(ref len);
		if (len == -1)
		{
			Values = default;
			return;
		}
		Values = new C(); // ToDo: use pool borrowing here?
		for (int i = 0; i < len; i++)
		{
			T val = default;
			unpacker.Read(ref val);
			Values.Add(val);
		}
	}
}

internal sealed class ValueArrayCommand<T> : CollectionCommand, IMemoryPackable where T : unmanaged
{
	public T[]? Values;

	public override int? Length => Values?.Length;

	public override IEnumerable? UntypedCollection => Values;

	public override Type StoredType => typeof(T);

	public override Type CollectionType => typeof(T[]);

	public override void Pack(ref MemoryPacker packer)
	{
		var len = Values?.Length ?? -1;
		packer.Write(len);
		if (Values != null)
		{
			Span<T> data = packer.Access<T>(len);
			Values.CopyTo(data);
		}
	}

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
		int len = 0;
		unpacker.Read(ref len);
		if (len == -1)
		{
			Values = null;
			return;
		}
		Values = new T[len]; // ToDo: use pool borrowing here?
		ReadOnlySpan<T> data = unpacker.Access<T>(len);
		data.CopyTo(Values);
	}
}

internal sealed class TypeRegistrationCommand : TypeCommand
{
	public override string ToString()
	{
		return $"TypeRegistrationCommand:{Type?.Name ?? "NULL"}<{string.Join(",", (IEnumerable<Type>?)Type?.GenericTypeArguments ?? [])}>";
	}
}

internal class TypeCommand : IMemoryPackable
{
	public Type? Type;
	private static Dictionary<string, Type> _typeCache = new();

	public void Pack(ref MemoryPacker packer)
	{
		PackType(Type, ref packer);
	}

	public void Unpack(ref MemoryUnpacker unpacker)
	{
		Type = UnpackType(ref unpacker);
	}

	private void PackType(Type? type, ref MemoryPacker packer)
	{
		if (type is null)
		{
			packer.Write(false);
		}
		else
		{
			packer.Write(true);
			if (type.IsGenericType)
			{
				packer.Write(true);
				var genericTypeDefinition = type.GetGenericTypeDefinition();
				packer.Write(genericTypeDefinition.FullName!);
				var typeArgs = type.GetGenericArguments();
				packer.Write(typeArgs.Length);
				foreach (var typeArg in typeArgs)
				{
					PackType(typeArg, ref packer);
				}
			}
			else
			{
				packer.Write(false);
				packer.Write(type.FullName!);
			}
		}
	}

	private Type? UnpackType(ref MemoryUnpacker unpacker)
	{
		var hasType = unpacker.Read<bool>();
		if (!hasType) return null;

		var isGenericType = unpacker.Read<bool>();
		if (isGenericType)
		{
			var genericTypeDefinitionName = unpacker.ReadString();
			int numTypeArgs = unpacker.Read<int>();
			var typeArgs = new Type?[numTypeArgs];
			for (int i = 0; i < numTypeArgs; i++)
			{
				typeArgs[i] = UnpackType(ref unpacker);
			}

			if (typeArgs.Any(t => t is null)) return null;

			var genericTypeDefinition = FindType(genericTypeDefinitionName);
			if (genericTypeDefinition != null)
			{
				return genericTypeDefinition.MakeGenericType(typeArgs!);
			}
			else
			{
				return null;
			}
		}
		else
		{
			var typeString = unpacker.ReadString();
			return FindType(typeString);
		}
	}

	private Type? FindType(string typeString)
	{
		if (_typeCache.TryGetValue(typeString, out var type))
		{
			return type;
		}
		
		type = Type.GetType(typeString);
		if (type is null)
		{
			foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
			{
				type = asm.GetType(typeString);
				if (type != null) break;
			}
		}
		if (type != null)
		{
			_typeCache[typeString] = type;
		}
		return type;
	}

	public override string ToString()
	{
		return $"TypeCommand:{Type?.Name ?? "NULL"}<{string.Join(",", (IEnumerable<Type>?)Type?.GenericTypeArguments ?? [])}>";
	}
}

internal sealed class StringArrayCommand : CollectionCommand
{
	public string?[]? Strings;

	public override int? Length => Strings?.Length;

	public override IEnumerable? UntypedCollection => Strings;
	public override Type StoredType => typeof(string);
	public override Type CollectionType => typeof(string[]);

	public override void Pack(ref MemoryPacker packer)
	{
		if (Strings is null)
		{
			packer.Write(-1);
			return;
		}
		int len = Strings.Length;
		packer.Write(len);
		foreach (var str in Strings)
		{
			packer.Write(str!);
		}
	}

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
		int len = 0;
		unpacker.Read(ref len);
		if (len == -1)
		{
			Strings = null;
			return;
		}
		Strings = new string[len]; // ToDo: use pool borrowing here?
		for (int i = 0; i < len; i++)
		{
			unpacker.Read(ref Strings[i]!);
		}
	}
}

internal sealed class StringCollectionCommand<C> : CollectionCommand where C : ICollection<string>?, new()
{
	public IReadOnlyCollection<string?>? Strings; // IReadOnlyCollection is required for covariance of string? and string
	public override int? Length => Strings?.Count;
	public override IEnumerable? UntypedCollection => Strings;
	public override Type StoredType => typeof(string);
	public override Type CollectionType => typeof(C);

	public override void Pack(ref MemoryPacker packer)
	{
		if (Strings is null)
		{
			packer.Write(-1);
			return;
		}
		int len = Strings.Count;
		packer.Write(len);
		foreach (var str in Strings)
		{
			packer.Write(str!);
		}
	}

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
		int len = 0;
		unpacker.Read(ref len);
		if (len == -1)
		{
			Strings = default;
			return;
		}
		var collection = new C(); // ToDo: use pool borrowing here?
		for (int i = 0; i < len; i++)
		{
			string? str = default;
			unpacker.Read(ref str!);
			collection.Add(str);
		}
		Strings = (IReadOnlyCollection<string?>?)collection;
	}
}

internal sealed class ObjectCollectionCommand<C, T> : CollectionCommand where C : ICollection<T>?, new() where T : class?, IMemoryPackable?, new()
{
	public C? Objects;
	public override int? Length => Objects?.Count;
	public override IEnumerable? UntypedCollection => Objects;
	public override Type StoredType => typeof(T);
	public override Type CollectionType => typeof(C);

	public override void Pack(ref MemoryPacker packer)
	{
		if (Objects is null)
		{
			packer.Write(-1);
			return;
		}
		int len = Objects.Count;
		packer.Write(len);
		foreach (var obj in Objects)
		{
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
#pragma warning disable CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
            packer.WriteObject(obj);
#pragma warning restore CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
#pragma warning restore CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
        }
	}

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
		int len = 0;
		unpacker.Read(ref len);
		if (len == -1)
		{
			Objects = default;
			return;
		}
		Objects = new C(); // ToDo: use pool borrowing here?
		for (int i = 0; i < len; i++)
		{
			T obj = new();
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
#pragma warning disable CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
            unpacker.ReadObject(ref obj!);
#pragma warning restore CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
#pragma warning restore CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
            Objects.Add(obj);
		}
	}
}

internal sealed class ObjectArrayCommand<T> : CollectionCommand where T : class?, IMemoryPackable?, new()
{
	public T[]? Objects;
	public override int? Length => Objects?.Length;

	public override IEnumerable? UntypedCollection => Objects;
	public override Type StoredType => typeof(T);
	public override Type CollectionType => typeof(T[]);

	public override void Pack(ref MemoryPacker packer)
	{
		if (Objects is null)
		{
			packer.Write(-1);
			return;
		}
		int len = Objects.Length;
		packer.Write(len);
		foreach (var obj in Objects)
		{
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
#pragma warning disable CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
            packer.WriteObject(obj);
#pragma warning restore CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
#pragma warning restore CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
        }
	}

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
		int len = 0;
		unpacker.Read(ref len);
		if (len == -1)
		{
			Objects = null;
			return;
		}
		Objects = new T[len]; // ToDo: use pool borrowing here?
		for (int i = 0; i < len; i++)
		{
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
#pragma warning disable CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
            unpacker.ReadObject(ref Objects[i]!);
#pragma warning restore CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
#pragma warning restore CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
        }
	}
}

internal sealed class ObjectCommand<T> : ObjectCommand where T : class?, IMemoryPackable?, new()
{
	public T? Object;

	public override object? UntypedObject => Object;
	public override Type ObjectType => typeof(T);

	public override void Pack(ref MemoryPacker packer)
	{
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
#pragma warning disable CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
        packer.WriteObject(Object);
#pragma warning restore CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
#pragma warning restore CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
    }

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
#pragma warning disable CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
#pragma warning disable CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
        unpacker.ReadObject(ref Object);
#pragma warning restore CS8634 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'class' constraint.
#pragma warning restore CS8631 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match constraint type.
    }
}

internal sealed class ValueCommand<T> : ValueCommand where T : unmanaged
{
	public T Value;

	public override object UntypedValue => Value;
	public override Type ValueType => typeof(T);

	public override void Pack(ref MemoryPacker packer)
	{
		packer.Write(Value);
	}

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
		unpacker.Read(ref Value);
	}
}

internal sealed class StringCommand : IMemoryPackable
{
	public string? String;

	public void Pack(ref MemoryPacker packer)
	{
		packer.Write(String!);
	}

	public void Unpack(ref MemoryUnpacker unpacker)
	{
		unpacker.Read(ref String!);
	}

	public override string ToString()
	{
		return $"StringCommand:{String ?? "NULL"}";
	}
}

internal sealed class QueueOwnerInitCommand : IMemoryPackable
{
	public void Pack(ref MemoryPacker packer)
	{
	}

	public void Unpack(ref MemoryUnpacker unpacker)
	{
	}
}

internal sealed class WrapperCommand : RendererCommand
{
	public int TypeIndex;
	public IMemoryPackable? Packable;
	public string? Owner;
	public string? Id;

	private static List<Type> CurrentRendererCommandTypes => (List<Type>)typeof(PolymorphicMemoryPackableEntity<RendererCommand>).GetField("types", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!  ?? throw new MissingFieldException("types");

	static WrapperCommand()
	{
		new RendererInitData(); // Trigger RendererCommand static constructor
		var list = new List<Type>();
		list.AddRange(CurrentRendererCommandTypes);
		list.Add(typeof(WrapperCommand));
		InitTypes(list);
	}

	public override void Pack(ref MemoryPacker packer)
	{
		if (Packable is null) throw new ArgumentNullException(nameof(Packable));
		if (Id is null) throw new ArgumentNullException(nameof(Id));
		if (Owner is null) throw new ArgumentNullException(nameof(Owner));

		packer.Write(Owner!);

		packer.Write(Id!);

		packer.Write(TypeIndex);
		
		Packable!.Pack(ref packer);
	}

	public override void Unpack(ref MemoryUnpacker unpacker)
	{
		var queue = (MessagingQueue)unpacker.Pool; // dirty hack

		unpacker.Read(ref Owner!);
		if (!queue.HasOwner(Owner)) throw new InvalidDataException($"Cannot unpack for unregistered owner: {Owner}");

		var ownerData = queue.GetOwnerData(Owner);

		unpacker.Read(ref Id!);

		unpacker.Read(ref TypeIndex);

		Packable = ownerData.IncomingTypeManager.Borrow(TypeIndex);
		Packable.Unpack(ref unpacker);
	}
}
