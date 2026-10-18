// The "table" under the board: dark technical surface with a faint hex-free grid.
shader_type spatial;
render_mode cull_back;

uniform vec4 base_color : source_color = vec4(0.10, 0.115, 0.135, 1.0);
uniform vec4 line_color : source_color = vec4(0.20, 0.26, 0.32, 1.0);
uniform float cell = 2.0;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 g = abs(fract(world_pos.xz / cell - 0.5) - 0.5) / fwidth(world_pos.xz / cell);
	float line = 1.0 - min(min(g.x, g.y), 1.0);
	float fade = clamp(1.0 - length(world_pos.xz) / 90.0, 0.0, 1.0);
	ALBEDO = mix(base_color.rgb, line_color.rgb, line * 0.6 * fade);
	ROUGHNESS = 0.9;
}
