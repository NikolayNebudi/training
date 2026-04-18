shader_type canvas_item;
render_mode blend_mix;

// Каждый сегмент червя — один MultiMesh-инстанс (квад). 5 инстансов = 1 червь.
// INSTANCE_CUSTOM:
//   r = hunger (0=голодный, 1=сытый) — определяет тон тела.
//   g = is_head flag (0/1) — голова темнее, с красными «глазами».
//   b = age_t (0..1) — старые черви бледнее.
//   a = state_flag (0=обычный, 1=копает, 2=охотится, 3=ест) — пульсация.

uniform vec3 body_dark    : source_color = vec3(0.20, 0.10, 0.10);  // тёмно-красно-бурый
uniform vec3 body_bright  : source_color = vec3(0.45, 0.30, 0.25);  // более тёплый
uniform vec3 hungry_tint  : source_color = vec3(0.30, 0.20, 0.35);  // фиолетовый когда голодный
uniform vec3 eye_color    : source_color = vec3(1.00, 0.20, 0.10);  // красные глаза
uniform float head_eye_size : hint_range(0.05, 0.4) = 0.18;

varying vec4 v_custom;

void vertex() {
    v_custom = INSTANCE_CUSTOM;
}

void fragment() {
    vec2 c = (UV - vec2(0.5)) * 2.0;       // -1..1
    float dist = length(c);
    if (dist > 1.0) {
        discard;
    } else {
        float hunger = v_custom.r;
        float is_head = v_custom.g;
        float age_t = v_custom.b;
        float state = v_custom.a;

        // Цвет тела: голодный фиолетовый ↔ сытый бурый.
        vec3 base = mix(hungry_tint, mix(body_dark, body_bright, hunger), 0.5 + 0.5 * hunger);
        base = mix(base, base * 0.7, age_t * 0.6);  // старость гасит цвет

        // Округлая «капсула» с шейдингом по краям.
        float shade = smoothstep(1.0, 0.2, dist);
        vec3 col = base * (0.45 + 0.55 * shade);

        // Голова темнее и крупнее «зрачком», плюс красные глаза.
        if (is_head > 0.5) {
            col *= 0.85;
            vec2 eyeL = vec2(-0.32, -0.18);
            vec2 eyeR = vec2( 0.32, -0.18);
            float eyeDist = min(length(c - eyeL), length(c - eyeR));
            if (eyeDist < head_eye_size) {
                float t = smoothstep(head_eye_size, 0.0, eyeDist);
                col = mix(col, eye_color, t);
            }
        }

        // Когда копает или ест — лёгкая пульсация.
        if (state > 0.5) {
            col *= 1.0 + 0.20 * sin(TIME * 4.0 + UV.x * 3.14);
        }

        float alpha = smoothstep(1.0, 0.85, dist);
        COLOR = vec4(col, alpha);
    }
}
