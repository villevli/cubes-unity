using System;
using UnityEngine;

namespace Cubes
{
    // TODO: Unity's asset GC does not see these references so calling UnloadUnusedAssets can break these

    /// <summary>
    /// Reference unity objects in unmanaged code.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    [Serializable]
    public struct UnityObjectRef<T> : IEquatable<UnityObjectRef<T>>
        where T : UnityEngine.Object
    {
        [SerializeField]
        private int instanceId;

        public UnityObjectRef(T instance)
        {
            instanceId = instance?.GetInstanceID() ?? 0;
        }

        public static implicit operator UnityObjectRef<T>(T instance)
        {
            return new UnityObjectRef<T>(instance);
        }

        public static implicit operator T(UnityObjectRef<T> unityObjectRef)
        {
            if (unityObjectRef.instanceId == 0)
                return null;
            return Resources.InstanceIDToObject(unityObjectRef.instanceId) as T;
        }

        public T Value
        {
            readonly get => this;
            set => this = value;
        }

        public readonly bool IsValid()
        {
            return Resources.InstanceIDIsValid(instanceId);
        }

        public bool Equals(UnityObjectRef<T> other)
        {
            return instanceId == other.instanceId;
        }

        public override bool Equals(object obj)
        {
            return obj is UnityObjectRef<T> other && Equals(other);
        }

        public static implicit operator bool(UnityObjectRef<T> obj)
        {
            return obj.IsValid();
        }

        public override int GetHashCode()
        {
            return instanceId.GetHashCode();
        }

        public static bool operator ==(UnityObjectRef<T> left, UnityObjectRef<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(UnityObjectRef<T> left, UnityObjectRef<T> right)
        {
            return !left.Equals(right);
        }
    }
}
