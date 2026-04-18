shader_type canvas_item;
render_mode blend_add;

// Каждый инстанс MultiMesh — квадрат, на котором рисуется радиальный glow.
// INSTANCE_CUSTOM (Color, передаётся через MultiMesh.SetInstanceCustomData):
//   r = glow_phase  (0..1, дыхание света)
//   g = hunger      (0=голодный, 1=сытый) — определяет цвет
//   b = age_t       (0=юный, 1=старый)
//   a = alive flag  (0..1, можно использовать для скрытия)
//
// Аддитивный blend — светлячки добавляют свет к сцене, не закрывают её.

uniform vec3 healthy_color : source_color = vec3(0.55, 1.00, 0.40);  // тёпло-зелёный (молодой, сытый)
uniform vec3 hungry_color  : source_color = vec3(0.45, 0.75, 1.00);  // бледно-голубой (голодный)
uniform vec3 old_color     : source_color = vec3(1.00, 0.85, 0.45);  // янтарный (старый)
uniform float body_size    : hint_range(0.0, 0.5) = 0.12;
uniform float halo_intensity : hint_range(0.0, 2.0) = 0.55;
uniform float body_intensity : hint_range(0.0, 3.0) = 1.20;

// INSTANCE_CUSTOM в canvas_item shaders доступен только в vertex(),
// поэтому пробрасываем во fragment через varying.
varying vec4 v_custom;

void vertex() {
    v_custom = INSTANCE_CUSTOM;
}

void fragment() {
    vec2 from_center = (UV - vec2(0.5)) * 2.0;       // -1..1
    float dist = length(from_center);
    if (dist > 1.0) {
        discard;
    } else {
        float pulse  = v_custom.r;
        float hunger = v_custom.g;
        float age_t  = v_custom.b;

        // Цвет: сытый молодой → healthy, голодный → hungry, старый → old
        vec3 base = mix(hungry_color, healthy_color, hunger);
        base = mix(base, old_color, age_t * 0.5);  // старость лишь тонирует

        // Тело: маленькая яркая точка в центре
        float body = smoothstep(body_size, 0.0, dist) * body_intensity;
        // Гало: широкий мягкий glow
        float halo = smoothstep(1.0, body_size, dist) * halo_intensity;

        float total = body + halo * (0.7 + 0.3 * pulse);
        COLOR = vec4(base * total, total);
    }
}
