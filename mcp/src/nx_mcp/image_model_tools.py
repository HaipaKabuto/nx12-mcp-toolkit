# Publication copy modified by repository maintainer on 2026-11-10.
# Original authors and licenses: docs/ATTRIBUTION.md.
"""Explicit drawing interpretation -> checked NX12 feature construction.

This module does not perform OCR. Image interpretation is supplied by the
caller/model and is recorded as such; unknown dimensions are never invented.
All modeling goes through the existing typed bridge commands, never NXOpen.
"""
from __future__ import annotations

from dataclasses import asdict, is_dataclass
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
from typing import Any, Awaitable, Callable, Literal

from mcp.server.mcpserver import MCPServer
from mcp.server.mcpserver.exceptions import ToolError as MCPToolError

Call = Callable[[str, dict[str, Any]], Awaitable[dict[str, Any]]]
Family = Literal["stepped_shaft", "flange", "bracket", "box", "gear"]
IMAGE_MODEL_TOOL_NAMES = {"nx_drawing_plan", "nx_model_from_plan", "nx_model_check"}
_REQUIRED = {
    "stepped_shaft": {"segments", "bore_diameter"},
    "flange": {"outer_diameter", "thickness", "bore_diameter", "hub_diameter", "hub_height", "hole_count", "hole_diameter", "bolt_circle_diameter", "start_angle_degrees"},
    "bracket": {"width", "height", "wall_thickness", "depth"},
    "box": {"length", "width", "height", "wall_thickness", "open_top"},
    "gear": {"module", "tooth_count", "pressure_angle_degrees", "face_width", "bore_diameter", "addendum_coefficient", "dedendum_coefficient", "backlash", "chord_tolerance"},
}


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()


def _plain(value: Any) -> Any:
    if is_dataclass(value):
        return _plain(asdict(value))
    if isinstance(value, dict):
        return {str(k): _plain(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [_plain(v) for v in value]
    if hasattr(value, "model_dump"):
        return _plain(value.model_dump())
    return value


def _identifier(value: Any) -> str:
    result = value.get("id") if isinstance(value, dict) else getattr(value, "id", None)
    if not result:
        raise MCPToolError("NX did not return an explicit object identifier")
    return str(result)


def _number(value: Any, name: str, *, zero: bool = False) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(name + " must be an explicit finite number")
    result = float(value)
    if result < 0 or (not zero and result == 0):
        raise ValueError(name + (" must be nonnegative" if zero else " must be positive"))
    return result


def _integer(value: Any, name: str, *, zero: bool = False) -> int:
    result = _number(value, name, zero=zero)
    if result != int(result):
        raise ValueError(name + " must be an integer")
    return int(result)


def _image(path: Path) -> str:
    if not path.is_file():
        raise MCPToolError("The stated source image does not exist inside the workspace")
    data = path.read_bytes()
    suffix = path.suffix.lower()
    valid = (
        suffix == ".png" and data.startswith(b"\x89PNG\r\n\x1a\n")
        or suffix in {".jpg", ".jpeg"} and data.startswith(b"\xff\xd8\xff")
        or suffix == ".webp" and data.startswith(b"RIFF") and data[8:12] == b"WEBP"
        or suffix == ".svg" and b"<svg" in data[:8192].lower()
    )
    if not valid:
        raise MCPToolError("Source must be a nonempty PNG, JPEG, WebP, or SVG image")
    return hashlib.sha256(data).hexdigest().upper()


def _geometry(family: str, dimensions: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any], list[dict[str, Any]]]:
    d: dict[str, Any] = {}
    operations: list[dict[str, Any]] = []
    if family == "stepped_shaft":
        source = dimensions["segments"]
        if not isinstance(source, list) or not source:
            raise ValueError("segments must be a nonempty axial-order list")
        segments = []
        for index, segment in enumerate(source):
            if not isinstance(segment, dict) or set(segment) != {"diameter", "length"}:
                raise ValueError("Each segment must have exactly diameter and length; missing/extra details need review")
            segments.append({"diameter": _number(segment["diameter"], f"segments[{index}].diameter"), "length": _number(segment["length"], f"segments[{index}].length")})
        bore = _number(dimensions["bore_diameter"], "bore_diameter", zero=True)
        if bore >= min(s["diameter"] for s in segments):
            raise ValueError("bore_diameter must be smaller than every shaft segment diameter")
        d = {"segments": segments, "bore_diameter": bore}
        length = sum(s["length"] for s in segments)
        radius = max(s["diameter"] for s in segments) / 2
        volume = 0.0
        first_moment = 0.0
        y = 0.0
        for segment in segments:
            v = math.pi / 4 * (segment["diameter"] ** 2 - bore ** 2) * segment["length"]
            volume += v
            first_moment += v * (y + segment["length"] / 2)
            y += segment["length"]
        expected = {"body_count": 1, "bbox_min": [-radius, 0.0, -radius], "bbox_max": [radius, length, radius], "volume": volume, "centroid": [0.0, first_moment / volume, 0.0], "length_unit": "mm", "volume_unit": "mm3"}
        operations = [{"operation": "closed_section_sketch", "plane": "XY", "axis": "Y", "source": "explicit segment diameters/lengths and bore"}, {"operation": "revolve", "angle_degrees": 360, "axis_start": [0, 0], "axis_end": [0, 1]}]
    elif family == "flange":
        for key in _REQUIRED[family] - {"hole_count", "start_angle_degrees"}:
            d[key] = _number(dimensions[key], key, zero=key in {"bore_diameter", "hub_diameter", "hub_height", "hole_diameter", "bolt_circle_diameter"})
        d["hole_count"] = _integer(dimensions["hole_count"], "hole_count", zero=True)
        angle = dimensions["start_angle_degrees"]
        if isinstance(angle, bool) or not isinstance(angle, (int, float)) or not math.isfinite(angle):
            raise ValueError("start_angle_degrees must be explicit and finite")
        d["start_angle_degrees"] = float(angle)
        if d["bore_diameter"] >= d["outer_diameter"]:
            raise ValueError("bore must be smaller than the flange outer diameter")
        if (d["hub_height"] == 0) != (d["hub_diameter"] == 0):
            raise ValueError("A missing hub must explicitly specify both hub_diameter=0 and hub_height=0")
        if d["hub_height"] and not d["bore_diameter"] < d["hub_diameter"] <= d["outer_diameter"]:
            raise ValueError("hub diameter must exceed bore diameter and fit inside the base")
        n, hole, pitch = d["hole_count"], d["hole_diameter"], d["bolt_circle_diameter"]
        if n:
            if not hole or not pitch:
                raise ValueError("A hole pattern needs positive hole and bolt-circle diameters")
            if pitch / 2 + hole / 2 >= d["outer_diameter"] / 2:
                raise ValueError("Bolt holes must remain fully inside the flange edge")
            if pitch / 2 - hole / 2 <= max(d["hub_diameter"], d["bore_diameter"]) / 2:
                raise ValueError("This flange builder requires bolt holes entirely outside the hub/bore")
            if n > 1 and 2 * (pitch / 2) * math.sin(math.pi / n) <= hole:
                raise ValueError("Adjacent bolt holes overlap; review the explicit dimensions")
        elif hole != 0 or pitch != 0:
            raise ValueError("No-hole flange must explicitly use hole_diameter=0 and bolt_circle_diameter=0")
        t, h = d["thickness"], d["hub_height"]
        vb = math.pi / 4 * (d["outer_diameter"] ** 2 - d["bore_diameter"] ** 2) * t
        vh = math.pi / 4 * (d["hub_diameter"] ** 2 - d["bore_diameter"] ** 2) * h if h else 0.0
        vhole = math.pi / 4 * hole ** 2 * t
        centers = [[pitch / 2 * math.cos(math.radians(d["start_angle_degrees"] + 360 * i / n)), pitch / 2 * math.sin(math.radians(d["start_angle_degrees"] + 360 * i / n))] for i in range(n)]
        volume = vb + vh - n * vhole
        radius = d["outer_diameter"] / 2
        expected = {"body_count": 1, "bbox_min": [-radius, -radius, 0.0], "bbox_max": [radius, radius, t + h], "volume": volume, "centroid": [-vhole * sum(c[0] for c in centers) / volume, -vhole * sum(c[1] for c in centers) / volume, (vb * t / 2 + vh * (t + h / 2) - n * vhole * t / 2) / volume], "hole_centers": centers, "length_unit": "mm", "volume_unit": "mm3"}
        operations = [{"operation": "base_circle_extrude", "diameter": d["outer_diameter"], "height": t}, {"operation": "hub_extrude_unite", "diameter": d["hub_diameter"], "height": h}, {"operation": "explicit_center_bore", "diameter": d["bore_diameter"]}, {"operation": "bolt_holes", "count": n, "centers": centers}]
    elif family == "bracket":
        d = {key: _number(dimensions[key], key) for key in _REQUIRED[family]}
        w, h, t, depth = d["width"], d["height"], d["wall_thickness"], d["depth"]
        if t >= min(w, h):
            raise ValueError("Bracket wall thickness must be below width and height")
        area = w * t + h * t - t * t
        expected = {"body_count": 1, "bbox_min": [0.0, 0.0, 0.0], "bbox_max": [w, h, depth], "volume": area * depth, "centroid": [(w * t * w / 2 + h * t * t / 2 - t * t * t / 2) / area, (w * t * t / 2 + h * t * h / 2 - t * t * t / 2) / area, depth / 2], "length_unit": "mm", "volume_unit": "mm3"}
        operations = [{"operation": "closed_L_section", "plane": "XY", "points": [[0, 0], [w, 0], [w, t], [t, t], [t, h], [0, h]]}, {"operation": "extrude", "distance": depth}]
    elif family == "box":
        d = {key: _number(dimensions[key], key) for key in _REQUIRED[family] - {"open_top"}}
        if dimensions["open_top"] is not True:
            raise ValueError("This builder supports an explicitly open-top box only")
        d["open_top"] = True
        length, width, height, t = d["length"], d["width"], d["height"], d["wall_thickness"]
        if 2 * t >= min(length, width) or t >= height:
            raise ValueError("Box wall thickness leaves no inner cavity")
        vo = length * width * height
        vi = (length - 2 * t) * (width - 2 * t) * (height - t)
        expected = {"body_count": 1, "bbox_min": [0.0, 0.0, 0.0], "bbox_max": [length, width, height], "volume": vo - vi, "centroid": [length / 2, width / 2, (vo * height / 2 - vi * (height + t) / 2) / (vo - vi)], "length_unit": "mm", "volume_unit": "mm3"}
        operations = [{"operation": "rectangle_extrude", "length": length, "width": width, "height": height}, {"operation": "inward_shell", "thickness": t, "remove": "actual top planar face selected by geometry"}]
    elif family == "gear":
        for key in _REQUIRED[family] - {"tooth_count"}:
            d[key] = _number(dimensions[key], key, zero=key in {"bore_diameter", "backlash"})
        d["tooth_count"] = _integer(dimensions["tooth_count"], "tooth_count")
        if not 0 < d["pressure_angle_degrees"] < 90:
            raise ValueError("Gear pressure angle must be between 0 and 90 degrees")
        accepted = dimensions.get("accept_polygonal_approximation", False)
        if not isinstance(accepted, bool):
            raise ValueError("accept_polygonal_approximation must be an explicit boolean")
        d["accept_polygonal_approximation"] = accepted
        if accepted:
            from nx_mcp.gear_geometry import build_gear_profile
            expected, profile = build_gear_profile(d)
            if profile["vertex_count"] > 2048:
                raise ValueError("The requested approximation exceeds the 2048-edge native sketch budget; choose another explicit tolerance")
            operations = [profile]
        else:
            expected = {"pitch_diameter": d["module"] * d["tooth_count"], "outside_diameter": d["module"] * (d["tooth_count"] + 2 * d["addendum_coefficient"]), "volume": None, "length_unit": "mm"}
            operations = [{"operation": "involute_profile_required", "chord_tolerance": d["chord_tolerance"], "runtime_builder": "requires explicit acceptance of bounded polygonal approximation; manufacturing-grade tooth roots are not implemented"}]
    else:
        raise ValueError("Unsupported family")
    return d, expected, operations


def _make_plan(source_image: str, source_hash: str, family: str, dimensions: dict[str, Any], units: str, material: str) -> dict[str, Any]:
    if units != "mm":
        return {"status": "needs_review", "reason": "Only explicit millimetre drawings are supported", "native_calls": 0}
    missing = sorted(key for key in _REQUIRED[family] if key not in dimensions or dimensions[key] is None)
    allowed = _REQUIRED[family] | ({"accept_polygonal_approximation"} if family == "gear" else set())
    unknown = sorted(set(dimensions) - allowed)
    if missing:
        return {"status": "needs_dimensions", "family": family, "missing_dimensions": missing, "unhandled_dimensions": unknown, "native_calls": 0, "assumptions": []}
    if unknown:
        return {"status": "needs_review", "family": family, "unhandled_dimensions": unknown, "reason": "Additional stated requirements must not be silently ignored", "native_calls": 0, "assumptions": []}
    try:
        normalized, expected, operations = _geometry(family, dimensions)
    except ValueError as error:
        return {"status": "invalid_dimensions", "family": family, "errors": [str(error)], "native_calls": 0, "assumptions": []}
    core = {"source_image": source_image, "source_sha256": source_hash, "family": family, "dimensions": normalized, "units": units, "material": material}
    plan_id = hashlib.sha256(json.dumps(core, sort_keys=True, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")).hexdigest().upper()
    return {**core, "status": "planning_only" if family == "gear" and not normalized.get("accept_polygonal_approximation") else "ready", "plan_id": plan_id, "interpretation_method": "caller_supplied_visual_interpretation; no automatic OCR", "dimension_provenance": {key: {"source": "explicit supplied interpretation of the identified source image", "value": value} for key, value in normalized.items() if key != "accept_polygonal_approximation"}, **({"modeling_choices": {"polygonal_approximation": {"accepted": normalized["accept_polygonal_approximation"], "source": "explicit caller acceptance of approximation" if normalized["accept_polygonal_approximation"] else "approximation not accepted", "is_drawing_dimension": False}}} if family == "gear" else {}), "material_status": "explicit" if material else "not_supplied", "assumptions": [], "expected": expected, "operations": operations, "limitations": (operations[0].get("limitations", ["Manufacturing-grade involute gear is not implemented; polygonal approximation needs explicit acceptance"])) if family == "gear" else ["Only the explicitly described family geometry is built; additional requirements cause review"], "native_calls": 0}


def add_image_model_tools(mcp: MCPServer, call: Call, resolve_path: Callable[[str], str]) -> None:
    def checked_plan(plan: dict[str, Any]) -> dict[str, Any]:
        family = plan.get("family")
        if family not in _REQUIRED:
            raise MCPToolError("Unknown or missing drawing family")
        source = str(plan.get("source_image") or "")
        source_hash = _image(Path(resolve_path(source)))
        regenerated = _make_plan(source, source_hash, family, plan.get("dimensions") or {}, str(plan.get("units") or ""), str(plan.get("material") or ""))
        if regenerated.get("status") != "ready":
            raise MCPToolError(json.dumps({"code": "NX_DRAWING_NOT_BUILDABLE", "plan": regenerated}, ensure_ascii=False))
        if plan.get("source_sha256") != source_hash or plan.get("plan_id") != regenerated["plan_id"]:
            raise MCPToolError("Source image or explicit dimensions changed; regenerate the drawing plan before building")
        return regenerated

    async def check(plan: dict[str, Any]) -> dict[str, Any]:
        status = _plain(await call("nx_status", {}))
        part = status.get("active_part")
        if not part:
            raise MCPToolError("No active NX part to validate")
        active_path = str(part.get("id") or "")
        if plan.get("model_path") and Path(active_path) != Path(resolve_path(str(plan["model_path"]))):
            raise MCPToolError("Active part does not match this model plan")
        attributes = _plain(await call("nx_list_attributes", {}))
        if plan["plan_id"] not in json.dumps(attributes, ensure_ascii=False):
            raise MCPToolError("Active part has no matching NX_MCP_PLAN_ID; refusing an unrelated-model validation")
        bodies = await call("nx_list_bodies", {})
        objects = bodies.get("objects") or []
        expected = plan["expected"]
        tests = [{"check": "body_count", "expected": expected["body_count"], "actual": len(objects), "passed": len(objects) == expected["body_count"]}]
        measured = None
        faces = None
        if len(objects) == 1:
            body_id = _identifier(objects[0])
            faces = _plain(await call("nx_list_faces", {"body_id": body_id}))
            measured = faces.get("body_properties")
            if not measured or measured.get("status") != "measured":
                raise MCPToolError("Actual body mass properties are unavailable; geometric acceptance cannot be inferred")
            for key in ("bbox_min", "bbox_max", "centroid"):
                actual = measured.get(key)
                target = expected[key]
                passed = isinstance(actual, list) and len(actual) == 3 and all(abs(a - b) <= 1e-3 + 1e-7 * abs(b) for a, b in zip(actual, target))
                tests.append({"check": key, "expected": target, "actual": actual, "passed": passed})
            actual_volume = measured.get("volume")
            volume_ok = isinstance(actual_volume, (int, float)) and abs(actual_volume - expected["volume"]) <= 1e-3 + 1e-7 * abs(expected["volume"])
            tests.append({"check": "volume", "expected": expected["volume"], "actual": actual_volume, "passed": volume_ok})
        features = _plain(await call("nx_list_features", {}))
        sketches = _plain(await call("nx_list_sketches", {}))
        tests.append({"check": "feature_and_sketch_presence", "actual": {"features": len(features.get("objects") or []), "sketches": len(sketches.get("objects") or [])}, "passed": bool(features.get("objects")) and bool(sketches.get("objects"))})
        return {"status": "passed" if all(t["passed"] for t in tests) else "failed", "active_part": part, "plan_id": plan["plan_id"], "tests": tests, "measured": measured, "feature_relationships": {"method": "explicit construction trace and native feature/sketch presence", "full_dependency_graph": "not exposed by the existing 50-tool interface", "features": features, "sketches": sketches}}

    @mcp.tool()
    async def nx_drawing_plan(source_image: str, family: Family, dimensions: dict[str, Any], units: Literal["mm"] = "mm", material: str = "") -> dict[str, Any]:
        """Plan a part from an image and explicitly interpreted dimensions; does not perform OCR.

        No NX mutation occurs. Missing dimensions return needs_dimensions, and
        unexpected requirements require review. The image hash binds provenance.
        """
        source_hash = _image(Path(resolve_path(source_image)))
        return _make_plan(source_image, source_hash, family, dimensions, units, material)

    @mcp.tool()
    async def nx_model_check(plan: dict[str, Any]) -> dict[str, Any]:
        """Read native geometry and check a matching image-plan part; no model mutation."""
        canonical = checked_plan(plan)
        if plan.get("model_path"):
            canonical["model_path"] = plan["model_path"]
        return await check(canonical)

    @mcp.tool()
    async def nx_model_from_plan(plan: dict[str, Any], path: str, export_step: bool = False) -> dict[str, Any]:
        """Build a new independent NX12 part using existing MCP bridge commands.

        Supports explicit stepped shafts, plain flanges, prismatic L brackets,
        and uniform open-top boxes. Explicitly accepted tolerance-bounded polygonal gears are supported; manufacturing-grade gears are not. Every
        mutating command checks the active part; partial failures retain a trace.
        """
        canonical = checked_plan(plan)
        target = Path(resolve_path(path))
        if target.suffix.lower() != ".prt" or target.exists():
            raise MCPToolError("Model destination must be a new .prt path inside the workspace")
        step_relative = str(Path(path).with_suffix(".stp"))
        step_target = Path(resolve_path(step_relative))
        trace_path = target.with_suffix(".model-trace.json")
        if trace_path.exists() or (export_step and step_target.exists()):
            raise MCPToolError("Model trace/STEP output already exists; choose a new independent file name")
        target.parent.mkdir(parents=True, exist_ok=True)
        trace: dict[str, Any] = {"started_at_utc": _now(), "target": str(target), "plan": canonical, "calls": [], "status": "running"}
        with trace_path.open("x", encoding="utf-8") as output:
            json.dump(trace, output, ensure_ascii=False, indent=2)

        def persist() -> None:
            trace_path.write_text(json.dumps(_plain(trace), ensure_ascii=False, indent=2, allow_nan=False), encoding="utf-8")

        async def raw(method: str, params: dict[str, Any]) -> dict[str, Any]:
            record = {"at_utc": _now(), "method": method, "params": params}
            trace["calls"].append(record)
            try:
                result = await call(method, params)
                record["result"] = _plain(result)
                persist()
                return result
            except Exception as error:
                record["error"] = str(error)
                persist()
                raise

        async def mutate(method: str, params: dict[str, Any]) -> dict[str, Any]:
            status = _plain(await raw("nx_status", {}))
            if Path(str((status.get("active_part") or {}).get("id") or "")) != target:
                raise MCPToolError("Active NX part changed; stopped before mutating an unrelated part")
            return await raw(method, params)

        async def sketch_lines(points: list[list[float]]) -> str:
            sketch = await mutate("nx_create_sketch", {"plane": "XY"})
            sid = _identifier(sketch["object"])
            compact = []
            for point in points:
                if not compact or point != compact[-1]:
                    compact.append(point)
            if compact[-1] == compact[0]:
                compact.pop()
            for index, first in enumerate(compact):
                second = compact[(index + 1) % len(compact)]
                await mutate("nx_sketch_line", {"sketch_id": sid, "start": {"x": first[0], "y": first[1]}, "end": {"x": second[0], "y": second[1]}})
            await mutate("nx_finish_sketch", {"sketch_id": sid})
            return sid

        async def circle_extrude(diameter: float, height: float, offset: float = 0.0) -> str:
            sketch = await mutate("nx_create_sketch", {"plane": "XY"})
            sid = _identifier(sketch["object"])
            await mutate("nx_sketch_circle", {"sketch_id": sid, "center": {"x": 0.0, "y": 0.0}, "diameter": diameter})
            await mutate("nx_finish_sketch", {"sketch_id": sid})
            result = await mutate("nx_extrude", {"sketch_id": sid, "distance": height, "start_offset": offset})
            return _identifier(result["body"])

        try:
            trace["previous_active_status"] = _plain(await raw("nx_status", {}))
            await raw("nx_create_part", {"path": str(target), "units": "mm"})
            d = canonical["dimensions"]
            family = canonical["family"]
            if family == "stepped_shaft":
                y = 0.0
                points = [[d["segments"][0]["diameter"] / 2, 0.0]]
                for index, segment in enumerate(d["segments"]):
                    y += segment["length"]
                    points.append([segment["diameter"] / 2, y])
                    if index + 1 < len(d["segments"]):
                        points.append([d["segments"][index + 1]["diameter"] / 2, y])
                points += [[d["bore_diameter"] / 2, y], [d["bore_diameter"] / 2, 0.0]]
                sid = await sketch_lines(points)
                await mutate("nx_revolve", {"sketch_id": sid, "axis_start": {"x": 0.0, "y": 0.0}, "axis_end": {"x": 0.0, "y": 1.0}, "angle": 360.0})
            elif family == "flange":
                body_id = await circle_extrude(d["outer_diameter"], d["thickness"])
                if d["hub_height"]:
                    hub_id = await circle_extrude(d["hub_diameter"], d["hub_height"], d["thickness"])
                    await mutate("nx_unite", {"target_body_id": body_id, "tool_body_ids": [hub_id]})
                if d["bore_diameter"]:
                    await mutate("nx_hole", {"body_id": body_id, "center": {"x": 0.0, "y": 0.0}, "diameter": d["bore_diameter"], "depth": d["thickness"] + d["hub_height"]})
                for x, y in canonical["expected"]["hole_centers"]:
                    await mutate("nx_hole", {"body_id": body_id, "center": {"x": x, "y": y}, "diameter": d["hole_diameter"], "depth": d["thickness"]})
            elif family == "bracket":
                sid = await sketch_lines(canonical["operations"][0]["points"])
                await mutate("nx_extrude", {"sketch_id": sid, "distance": d["depth"]})
            elif family == "gear":
                profile = canonical["operations"][0]
                if not d.get("accept_polygonal_approximation") or profile.get("manufacturing_grade") is not False:
                    raise MCPToolError("An exact manufacturing gear is not available; explicit approximation acceptance is required")
                if profile["vertex_count"] > 2048:
                    raise MCPToolError("The requested approximation exceeds the 2048-edge native sketch budget; choose another explicit tolerance")
                batch = await mutate("nx_image_polyline_sketch", {"expected_part": str(target), "points": profile["points"]})
                sid = str(batch.get("sketch_id") or "")
                if not sid or batch.get("restored") is not True:
                    raise MCPToolError("Native profile did not verify its sketch or restoration of the original NX sketch preference")
                result = await mutate("nx_extrude", {"sketch_id": sid, "distance": d["face_width"]})
                bid = _identifier(result["body"])
                if d["bore_diameter"]:
                    await mutate("nx_hole", {"body_id": bid, "center": {"x": 0.0, "y": 0.0}, "diameter": d["bore_diameter"], "depth": d["face_width"]})
                await mutate("nx_set_attribute", {"name": "GEAR_PROFILE_MODEL", "value": "Tolerance-bounded polygonal involute; sharp radial roots; not manufacturing-grade"})
                await mutate("nx_set_attribute", {"name": "GEAR_TOOTH_COUNT", "value": str(d["tooth_count"])})
                await mutate("nx_set_attribute", {"name": "GEAR_PROFILE_BOUND_MM", "value": str(profile["maximum_profile_approximation_bound_mm"])})
            elif family == "box":
                sketch = await mutate("nx_create_sketch", {"plane": "XY"})
                sid = _identifier(sketch["object"])
                await mutate("nx_sketch_rectangle", {"sketch_id": sid, "corner1": {"x": 0.0, "y": 0.0}, "corner2": {"x": d["length"], "y": d["width"]}})
                await mutate("nx_finish_sketch", {"sketch_id": sid})
                result = await mutate("nx_extrude", {"sketch_id": sid, "distance": d["height"]})
                bid = _identifier(result["body"])
                face_data = await raw("nx_list_faces", {"body_id": bid})
                top = [face for face in face_data.get("faces", []) if face.get("face_type") == "Planar" and face.get("normal") and face["normal"][2] > 0.999999 and face.get("centroid") and abs(face["centroid"][2] - d["height"]) < 1e-6]
                if len(top) != 1:
                    raise MCPToolError("Cannot unambiguously identify the actual top face; no shell request sent")
                await mutate("nx_shell", {"body_id": bid, "thickness": d["wall_thickness"], "remove_face_index": top[0]["index"], "inward": True})
            else:
                raise MCPToolError("No verified builder for this drawing family")
            for name, value in {"NX_MCP_PLAN_ID": canonical["plan_id"], "NX_MCP_SOURCE_SHA256": canonical["source_sha256"], "NX_MCP_SOURCE_IMAGE": canonical["source_image"]}.items():
                await mutate("nx_set_attribute", {"name": name, "value": value})
            if canonical["material"]:
                await mutate("nx_set_attribute", {"name": "MATERIAL", "value": canonical["material"]})
            canonical["model_path"] = path
            validation = await check(canonical)
            trace["validation"] = validation
            if validation["status"] != "passed":
                raise MCPToolError("Native geometric readback does not match the explicit drawing plan")
            await mutate("nx_fit_view", {})
            await mutate("nx_save_part", {})
            if export_step:
                await mutate("nx_export_step", {"path": str(step_target)})
                if not step_target.is_file() or not step_target.read_bytes().startswith(b"ISO-10303-21;"):
                    raise MCPToolError("STEP command returned without a valid completed file")
            trace["status"] = "passed"
            trace["finished_at_utc"] = _now()
            persist()
            return {"status": "success", "path": str(target), "plan": canonical, "validation": validation, "trace_path": str(trace_path), "step_path": str(step_target) if export_step else None, "actual_mutations": sum(1 for r in trace["calls"] if r["method"] not in {"nx_status", "nx_list_faces", "nx_list_bodies", "nx_list_features", "nx_list_sketches", "nx_list_attributes"})}
        except Exception as error:
            trace["status"] = "partial_failure"
            trace["error"] = str(error)
            trace["finished_at_utc"] = _now()
            try:
                status = _plain(await raw("nx_status", {}))
                trace["failure_status"] = status
                if Path(str((status.get("active_part") or {}).get("id") or "")) == target:
                    trace["partial_bodies"] = _plain(await raw("nx_list_bodies", {}))
                    trace["partial_features"] = _plain(await raw("nx_list_features", {}))
                    await mutate("nx_save_part", {})
                    trace["partial_part_saved"] = True
            except Exception as reconcile_error:
                trace["reconciliation_error"] = str(reconcile_error)
            persist()
            raise MCPToolError(json.dumps({"code": "NX_IMAGE_MODEL_FAILED", "message": str(error), "partial_file": str(target), "partial_saved": trace.get("partial_part_saved", False), "trace_path": str(trace_path), "retryable": False}, ensure_ascii=False)) from error
