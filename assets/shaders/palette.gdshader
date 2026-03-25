shader_type canvas_item;

uniform sampler2D screen_texture : hint_screen_texture, filter_nearest;
uniform sampler2D palette_texture : filter_nearest;
uniform int palette_size : hint_range(0, 256) = 0;
uniform bool enabled = false;

void fragment() {
	vec4 screen_color = texture(screen_texture, SCREEN_UV);
	if (!enabled || palette_size <= 0) {
		COLOR = screen_color;
		return;
	}
	vec3 rgb = screen_color.rgb;
	float best_dist = 999999.0;
	vec3 best_color = rgb;
	for (int i = 0; i < palette_size; i++) {
		float u = (float(i) + 0.5) / float(textureSize(palette_texture, 0).x);
		vec3 pal = texture(palette_texture, vec2(u, 0.5)).rgb;
		vec3 d = rgb - pal;
		float dist = dot(d, d);
		if (dist < best_dist) {
			best_dist = dist;
			best_color = pal;
		}
	}
	COLOR = vec4(best_color, screen_color.a);
}
