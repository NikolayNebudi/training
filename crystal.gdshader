shader_type canvas_item;

// Sprite-носитель — Sprite2D с growth-текстурой 800×800 (L8). Значение в
// пикселе: 0 = ничего, 1..253 = растущий кристалл, 254..255 = зрелый блок.
//
// Шейдер рисует процедурный «алмаз» (Manhattan-SDF) внутри каждой клетки.
// Размер алмаза растёт с growth, цвет — от тёмно-синего (центр) к светло-
// циановому (край). Зрелые блоки полностью непрозрачны и слегка пульсируют.

uniform vec3  crystal_bright : source_color = vec3(0.65, 0.92, 1.0);
uniform vec3  crystal_dark   : source_color = vec3(0.08, 0.28, 0.55);
uniform float mature_threshold : hint_range(0.5, 1.0) = 0.99;
uniform float growth_to_size   : hint_range(0.1, 1.0) = 0.95;
uniform float pulse_speed      : hint_range(0.0, 5.0) = 0.7;
uniform float seed_glow_alpha  : hint_range(0.0, 1.0) = 0.85;

void fragment() {
    float growth = texture(TEXTURE, UV).r;
    if (growth < 0.01) {
        discard;
    } else {
        // Координата текущей клетки (texture pixel space) — ищем центр.
        vec2 tex_size = vec2(textureSize(TEXTURE, 0));
        vec2 cell = floor(UV * tex_size) + 0.5;
        vec2 cell_uv_center = cell / tex_size;
        vec2 to_center = (UV - cell_uv_center) * tex_size;  // -0.5..0.5

        // Manhattan SDF — даёт diamond shape.
        float dist_diamond = abs(to_center.x) + abs(to_center.y);

        float size = growth * growth_to_size * 0.5;
        if (dist_diamond > size) {
            discard;
        } else {
            float t = 1.0 - dist_diamond / max(size, 0.001);
            bool is_mature = growth >= mature_threshold;

            vec3 col;
            float alpha;

            if (is_mature) {
                // Зрелый блок: solid с лёгкой пульсацией.
                col = mix(crystal_dark, crystal_bright, t * 0.7 + 0.3);
                col *= 1.0 + 0.10 * sin(TIME * pulse_speed + cell.x * 0.7 + cell.y * 1.3);
                alpha = 1.0;
            } else {
                // Растущий: glowy, полупрозрачный.
                col = mix(crystal_dark, crystal_bright, t);
                col *= 1.0 + 0.20 * sin(TIME * pulse_speed * 2.0 + cell.x * 0.7);
                alpha = growth * seed_glow_alpha;
            }

            COLOR = vec4(col, alpha);
        }
    }
}
