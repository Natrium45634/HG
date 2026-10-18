// Technological barrier: translucent emissive prism with moving scan lines.
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never, shadows_disabled;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float scan = 0.55 + 0.45 * sin(world_pos.y * 40.0 - TIME * 4.0);
	float band = smoothstep(0.92, 1.0, fract(world_pos.y * 2.0 - TIME * 0.6));
	vec3 c = COLOR.rgb * (0.35 + 0.65 * scan) * (1.0 + UV.x * 0.4) + COLOR.rgb * band * 1.5;
	ALBEDO = c;
	ALPHA = COLOR.a * (0.55 + 0.45 * scan);
}
