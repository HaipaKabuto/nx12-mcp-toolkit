# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Explicit, tolerance-bounded polygonal external involute gear profile.

This is a geometric demonstration profile, not a hob-generated/trochoidal or
manufacturing-grade gear. All engineering dimensions are explicit inputs.
"""
from __future__ import annotations
import math
from typing import Any

_QUANTIZATION = math.sqrt(2.0) * 0.5e-6  # existing nx_sketch_line transport is6dp
_MAX_VERTICES = 8192


def _polar(radius: float, angle: float) -> list[float]:
    return [radius * math.cos(angle), radius * math.sin(angle)]


def _segment_distance(point: list[float], end: list[float]) -> float:
    dx, dy = end[0] - point[0], end[1] - point[1]
    fraction = max(0.0, min(1.0, -(point[0] * dx + point[1] * dy) / (dx * dx + dy * dy)))
    return math.hypot(point[0] + fraction * dx, point[1] + fraction * dy)


def build_gear_profile(d: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any]]:
    """Return polygon-derived acceptance values and explicit construction data.

    Inputs have already undergone finite/positive validation in the planner.
    Backlash means the reduction of circular tooth thickness at the pitch circle.
    """
    module, teeth = d["module"], d["tooth_count"]
    alpha = math.radians(d["pressure_angle_degrees"])
    tolerance = d["chord_tolerance"]
    if teeth < 4:
        raise ValueError("This demonstration profile requires at least4 teeth")
    if tolerance <= 2.0 * _QUANTIZATION:
        raise ValueError("Requested gear tolerance is below the existing six-decimal sketch transport resolution")
    budget = tolerance - _QUANTIZATION
    pitch_radius = module * teeth / 2.0
    base_radius = pitch_radius * math.cos(alpha)
    tip_radius = pitch_radius + module * d["addendum_coefficient"]
    root_radius = pitch_radius - module * d["dedendum_coefficient"]
    if root_radius <= 0 or tip_radius <= base_radius:
        raise ValueError("Explicit gear radii do not define a positive root and usable involute")
    bore_radius = d["bore_diameter"] / 2.0
    if bore_radius >= root_radius:
        raise ValueError("Bore diameter must remain inside the root circle")
    tooth_thickness = math.pi * module / 2.0 - d["backlash"]
    if tooth_thickness <= 0:
        raise ValueError("Backlash removes the entire stated pitch-circle tooth thickness")
    half_pitch_thickness = tooth_thickness / (2.0 * pitch_radius)
    base_half_angle = half_pitch_thickness + math.tan(alpha) - alpha
    first_radius = max(base_radius, root_radius)
    t_start = math.sqrt(max(0.0, (first_radius / base_radius) ** 2 - 1.0))
    t_end = math.sqrt((tip_radius / base_radius) ** 2 - 1.0)
    involute = lambda t: t - math.atan(t)
    root_half_angle = base_half_angle - involute(t_start)
    tip_half_angle = base_half_angle - involute(t_end)
    pitch = 2.0 * math.pi / teeth
    if tip_half_angle <= 0:
        raise ValueError("Explicit dimensions produce pointed/crossing tooth tips")
    if not 0 < 2.0 * root_half_angle < pitch:
        raise ValueError("Explicit dimensions make neighboring tooth roots overlap")

    # For the involute parameterization P(t), ||P''(t)||=rb*sqrt(1+t²).
    # Linear interpolation error <=max||P''||*(tb-ta)²/8, including t=0.
    parameters = [t_start]
    bounds = []

    def subdivide(a: float, b: float, depth: int = 0) -> None:
        bound = base_radius * math.sqrt(1.0 + b * b) * (b - a) ** 2 / 8.0
        if bound <= budget:
            parameters.append(b)
            bounds.append(bound)
        elif depth >= 30:
            raise ValueError("Involute sampling could not satisfy the explicit tolerance")
        else:
            mid = (a + b) / 2.0
            subdivide(a, mid, depth + 1)
            subdivide(mid, b, depth + 1)

    subdivide(t_start, t_end)
    raw: list[list[float]] = []
    arc_bounds: list[float] = []

    def add(point: list[float]) -> None:
        if not raw or math.hypot(point[0] - raw[-1][0], point[1] - raw[-1][1]) > 1e-12:
            raw.append(point)
        if len(raw) > _MAX_VERTICES:
            raise ValueError("Explicit tolerance/tooth count exceeds the8192-edge safe MCP profile budget")

    def arc(radius: float, start: float, end: float, center: float | None = None) -> None:
        # Exact circular sagitta criterion; forced tooth-center/cardinal knots
        # also preserve the selected polygon's physical extrema when available.
        knots = [start, end]
        if center is not None and start < center < end:
            knots.append(center)
        for k in range(math.floor(start / (math.pi / 2)) - 1, math.ceil(end / (math.pi / 2)) + 2):
            angle = k * math.pi / 2
            if start < angle < end:
                knots.append(angle)
        knots.sort()
        cosine = max(-1.0, min(1.0, 1.0 - budget / radius))
        maximum_step = 2.0 * math.acos(cosine)
        for a, b in zip(knots, knots[1:]):
            count = max(1, math.ceil((b - a) / maximum_step))
            delta = (b - a) / count
            arc_bounds.append(radius * (1.0 - math.cos(delta / 2.0)))
            for i in range(count + 1):
                add(_polar(radius, a + delta * i))

    for tooth in range(teeth):
        angle = tooth * pitch
        add(_polar(root_radius, angle - root_half_angle))
        for t in parameters:
            radius = base_radius * math.sqrt(1.0 + t * t)
            half = base_half_angle - involute(t)
            add(_polar(radius, angle - half))
        arc(tip_radius, angle - tip_half_angle, angle + tip_half_angle, angle)
        for t in reversed(parameters):
            radius = base_radius * math.sqrt(1.0 + t * t)
            half = base_half_angle - involute(t)
            add(_polar(radius, angle + half))
        add(_polar(root_radius, angle + root_half_angle))
        arc(root_radius, angle + root_half_angle, angle + pitch - root_half_angle)

    points: list[list[float]] = []
    for point in raw:
        quantized = [float(format(value, ".6f")) for value in point]
        if not points or quantized != points[-1]:
            points.append(quantized)
    if points[-1] == points[0]:
        points.pop()
    if len(points) < 3:
        raise ValueError("Quantization collapsed the gear profile")
    cross_sum = 0.0
    moment_x = 0.0
    moment_y = 0.0
    minimum_clearance = float("inf")
    for index, p in enumerate(points):
        q = points[(index + 1) % len(points)]
        cross = p[0] * q[1] - q[0] * p[1]
        cross_sum += cross
        moment_x += (p[0] + q[0]) * cross
        moment_y += (p[1] + q[1]) * cross
        minimum_clearance = min(minimum_clearance, _segment_distance(p, q))
    area = cross_sum / 2.0
    if area <= 0 or bore_radius >= minimum_clearance - 1e-6:
        raise ValueError("The quantized outer profile cannot safely contain the stated bore")
    material_area = area - math.pi * bore_radius ** 2
    center_x = moment_x / (6.0 * area)
    center_y = moment_y / (6.0 * area)
    width = d["face_width"]
    expected = {
        "body_count": 1,
        "bbox_min": [min(p[0] for p in points), min(p[1] for p in points), 0.0],
        "bbox_max": [max(p[0] for p in points), max(p[1] for p in points), width],
        "volume": material_area * width,
        "centroid": [center_x * area / material_area, center_y * area / material_area, width / 2],
        "length_unit": "mm", "volume_unit": "mm3",
        "pitch_diameter": 2 * pitch_radius, "base_diameter": 2 * base_radius,
        "outside_diameter": 2 * tip_radius, "root_diameter": 2 * root_radius,
        "outer_profile_polygon_area": area,
        "acceptance_reference": "actual quantized polygon minus exact circular bore, not an exact analytic gear volume",
    }
    operation = {
        "operation": "tolerance_bounded_involute_polygon_extrude",
        "profile_model": "unshifted external spur gear; sharp radial root connectors; no trochoid or fillets",
        "manufacturing_grade": False,
        "points": points, "vertex_count": len(points),
        "involute_segments_per_flank": len(parameters) - 1,
        "requested_chord_tolerance_mm": tolerance,
        "involute_linear_interpolation_bound_mm": max(bounds),
        "circular_arc_sagitta_bound_mm": max(arc_bounds),
        "coordinate_quantization_bound_mm": _QUANTIZATION,
        "maximum_profile_approximation_bound_mm": max(bounds + arc_bounds) + _QUANTIZATION,
        "backlash_definition": "pitch-circle circular tooth-thickness reduction",
        "limitations": [
            "Polygonal approximation of involute flanks and tip/root circles within the stated geometric bound",
            "Radial root connectors are a declared modeling choice; generated trochoidal roots and undercut are not modeled",
            "No helix, profile shift, root fillet, gear accuracy grade, strength or mesh/contact acceptance",
        ],
    }
    return expected, operation
