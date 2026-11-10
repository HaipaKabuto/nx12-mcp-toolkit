# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Geometry type helpers for NX Open operations."""

from __future__ import annotations

from typing import Any


def make_point3d(x: float, y: float, z: float) -> Any:
    """Create an NXOpen.Point3d."""
    import NXOpen

    return NXOpen.Point3d(x, y, z)


def make_vector3d(x: float, y: float, z: float) -> Any:
    """Create an NXOpen.Vector3d."""
    import NXOpen

    return NXOpen.Vector3d(x, y, z)


def make_matrix3x3_identity() -> Any:
    """Create an identity NXOpen.Matrix3x3."""
    import NXOpen

    m = NXOpen.Matrix3x3()
    m.Xx, m.Xy, m.Xz = 1.0, 0.0, 0.0
    m.Yx, m.Yy, m.Yz = 0.0, 1.0, 0.0
    m.Zx, m.Zy, m.Zz = 0.0, 0.0, 1.0
    return m


def resolve_object_by_name(
    part: Any,
    name: str,
    *collections: Any,
) -> Any | None:
    """Find an NX object by name across multiple collections.

    Each collection in *collections should support .ToArray() returning
    objects with a .Name attribute.  Comparison is case-insensitive.

    Returns the first match, or None.
    """
    target = name.lower()
    for collection in collections:
        for obj in collection.ToArray():
            if obj.Name.lower() == target:
                return obj
    return None
