shader_type canvas_item;
render_mode blend_add;

// Sprite-носитель — Sprite2D с mushroom-текстурой 800×800 (L8). Значение
// в пикселе — индекс цвета палитры (1..6) либо 0 (нет гриба).
//
// Шейдер ищет центр клетки (Nearest filter обязателен!), рисует яркую
// маленькую «шляпку» + широкое мягкое гало по выбранному цвету палитры.
// Аддитивный blend → на тёмном полу даёт реальное «свечение».

uniform float body_radius : hint_range(0.05, 0.5) = 0.13;
uniform float halo_radius : hint_range(0.1, 1.5) = 0.85;
uniform float intensity   : hint_range(0.0, 3.0) = 1.20;
uniform float pulse_speed : hint_range(0.0, 5.0) = 1.0;
/// Минимальный множитель размера (per-cell случайный, deterministic).
uniform float size_min    : hint_range(0.2, 1.0) = 0.45;
/// Максимальный множитель размера.
uniform float size_max    : hint_range(0.5, 2.5) = 1.55;

// Deterministic per-cell hash for size variation
float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

void fragment() {
    float v = texture(TEXTURE, UV).r * 255.0;
    int idx = int(round(v));
    if (idx == 0) {
        discard;
    } else {
        vec2 tex_size = vec2(textureSize(TEXTURE, 0));
        vec2 cell = floor(UV * tex_size) + 0.5;
        vec2 cell_uv = cell / tex_size;
        vec2 to_center = (UV - cell_uv) * tex_size;     // -0.5..0.5
        float dist = length(to_center) * 2.0;            // 0..1 across cell

        // Per-cell детерминированный размер: одни грибы крошечные, другие
        // большие неоновые шляпы. Hash от cell координат — каждый раз тот же
        // результат для той же клетки.
        float sz = mix(size_min, size_max, hash21(cell * 0.073));
        float effective_body_radius = body_radius * sz;
        float effective_halo_radius = halo_radius * sz;

        if (dist > effective_halo_radius * 2.0) {
            discard;
        } else {
            // Палитра неоновых цветов
            vec3 col;
            if (idx == 1)      col = vec3(0.40, 1.00, 1.00);   // cyan
            else if (idx == 2) col = vec3(0.80, 0.35, 1.00);   // purple
            else if (idx == 3) col = vec3(1.00, 0.30, 0.75);   // magenta
            else if (idx == 4) col = vec3(0.65, 1.00, 0.30);   // lime
            else if (idx == 5) col = vec3(1.00, 0.55, 0.15);   // orange
            else               col = vec3(1.00, 0.40, 0.55);   // pink

            // Тело — маленькая яркая шляпка
            float body = smoothstep(effective_body_radius * 2.0, 0.0, dist) * 1.4;
            // Гало — широкий мягкий glow
            float halo = smoothstep(effective_halo_radius * 2.0, effective_body_radius * 2.0, dist) * 0.40;

            // Пульсация (немного отличается per-cell)
            float phase = TIME * pulse_speed + cell.x * 0.7 + cell.y * 1.3;
            float pulse = 0.85 + 0.15 * sin(phase);

            float total = (body + halo) * intensity * pulse;
            COLOR = vec4(col * total, total);
        }
    }
}
