// Edge lines, rims, patterns and stripes: unshaded vertex colour, UV.x = glow (HDR multiplier
// picked up by the environment glow), UV.y = pulse speed (0 = steady).
shader_type spatial;
render_mode unshaded, cull_disabled, blend_mix, depth_draw_opaque, shadows_disabled;

void fragment() {
	float pulse = UV.y > 0.0 ? 0.7 + 0.3 * sin(TIME * UV.y) : 1.0;
	ALBEDO = COLOR.rgb * (1.0 + UV.x * pulse);
	ALPHA = COLOR.a;
}
