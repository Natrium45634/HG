// Liquids (water, lava, acid): vertex colour = base colour + alpha, UV.x = glow strength,
// UV.y = animation (0 none, 1 flow, 2 pulse, 3 shimmer). Highlights from cheap value noise.
shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, specular_schlick_ggx;

uniform float time_scale = 1.0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float t = TIME * time_scale;
	float anim = UV.y;
	vec2 p = world_pos.xz * 1.6;
	float n;
	if (anim > 0.5 && anim < 1.5) {
		n = noise(p + vec2(t * 0.35, t * 0.2)) * 0.6 + noise(p * 2.3 - vec2(t * 0.5, 0.0)) * 0.4;
	} else {
		n = noise(p + vec2(t * 0.08, -t * 0.06)) * 0.5 + noise(p * 3.1 + vec2(-t * 0.12, t * 0.1)) * 0.5;
	}
	float pulse = anim > 1.5 && anim < 2.5 ? 0.75 + 0.25 * sin(t * 2.4 + world_pos.x * 0.7) : 1.0;
	vec3 base = COLOR.rgb;
	float hi = smoothstep(0.62, 0.95, n);
	ALBEDO = mix(base * (0.7 + 0.3 * n), vec3(1.0), hi * 0.25 * (1.0 - min(UV.x, 1.0)));
	ROUGHNESS = 0.08 + 0.2 * (1.0 - n);
	METALLIC = 0.05;
	SPECULAR = 0.7;
	float glow = UV.x;
	EMISSION = base * base * glow * (0.35 + 0.5 * n) * pulse + base * hi * glow * 0.12;
	ALPHA = COLOR.a;
}
