using System;

namespace OmniTwin.Contracts;

/// <summary>A finite position or velocity in the Unity X/Z plane.</summary>
public readonly record struct Vector2D
{
    private readonly float _x;
    private readonly float _z;

    /// <summary>Creates a finite two-dimensional value. The default value is the origin.</summary>
    public Vector2D(float x, float z) : this()
    {
        X = ContractGuard.Finite(x, nameof(x));
        Z = ContractGuard.Finite(z, nameof(z));
    }

    /// <summary>Gets the X component, in meters or meters per second according to context.</summary>
    public float X
    {
        get => _x;
        init => _x = ContractGuard.Finite(value, nameof(X));
    }

    /// <summary>Gets the Z component, in meters or meters per second according to context.</summary>
    public float Z
    {
        get => _z;
        init => _z = ContractGuard.Finite(value, nameof(Z));
    }

    /// <summary>Computes Euclidean distance using double intermediates to avoid float overflow.</summary>
    public double DistanceTo(Vector2D other)
    {
        var dx = (double)X - other.X;
        var dz = (double)Z - other.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
