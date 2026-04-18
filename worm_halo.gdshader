shader_type canvas_item;
render_mode blend_add;

// Аддитивное гало вокруг каждого сегмента червя. Делает червей видимыми
// в темноте — они светятся слабым багровым светом.
//
// INSTANCE_CUSTOM (тот же что у worm_body):
//   r = hunger, g = is_head, b = age_t, a = state_flag

uniform vec3 halo_head_color : source_color = vec3(0.95, 0.20, 0.10);   // красно-оранжевая голова
uniform vec3 halo_body_color : source_color = vec3(0.55, 0.10, 0.20);   // тёмно-багровое тело
uniform float intensity      : hint_range(0.0, 2.0) = 0.55;
uniform float pulse_speed    : hint_range(0.0, 5.0) = 1.5;

varying vec4 v_custom;

void vertex() {
    v_custom = INSTANCE_CUSTOM;
}

void fragment() {
    vec2 c = (UV - vec2(0.5)) * 2.0;
    float dist = length(c);
    if (dist > 1.0) {
        discard;
    } else {
        float is_head = v_custom.g;
        float age_t = v_custom.b;
        float state = v_custom.a;

        // Радиальный спад
        float halo = smoothstep(1.0, 0.0, dist);
        halo = pow(halo, 1.6);   // мягче край

        // Голова ярче и горячее
        vec3 col = mix(halo_body_color, halo_head_color, is_head);
        // Старость затемняет
        col *= 1.0 - age_t * 0.4;

        // Дыхание свечения
        float pulse = 0.85 + 0.15 * sin(TIME * pulse_speed + UV.x * 6.28 + is_head * 1.5);
        // При копании/поедании пульсирует ярче
        if (state > 0.5) pulse *= 1.0 + 0.30 * sin(TIME * 6.0);

        COLOR = vec4(col * halo * intensity * pulse, halo);
    }
}
