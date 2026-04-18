shader_type canvas_item;

// Sprite-носитель — Sprite2D с growth-текстурой 800×800 (L8). Значение в
// пикселе (0..255):
//   0..50    — Stage 0: крошечная точка-зерно
//   51..130  — Stage 1: одиночный шард-лезвие
//   131..220 — Stage 2: малый кластер (4 спира)
//   221..253 — Stage 3: большой кластер (6 спиров)
//   254..255 — Stage 4: зрелый шестигранный блок (физическая преграда)

uniform vec3 c_dark   : source_color = vec3(0.04, 0.16, 0.45);    // глубина граней
uniform vec3 c_bright : source_color = vec3(0.55, 0.88, 1.00);    // светлая фасет
uniform vec3 c_white  : source_color = vec3(0.92, 1.00, 1.00);    // блик/тип
uniform float pulse_speed : hint_range(0.0, 5.0) = 0.7;
uniform float spike_width_base : hint_range(0.01, 0.2) = 0.05;

// Возвращает intensity (0..1) для пиксела относительно одного «спира»,
// растущего из (0,0) в направлении spike_angle на длину length.
float spike_intensity(vec2 p, float spike_angle, float length, float width_base) {
    float ca = cos(spike_angle);
    float sa = sin(spike_angle);
    // Поворачиваем p так, чтобы спир был вдоль +X.
    vec2 r = vec2(ca * p.x + sa * p.y, -sa * p.x + ca * p.y);
    if (r.x < 0.0 || r.x > length) return 0.0;
    float w = width_base * (1.0 - r.x / length);
    if (w <= 0.0) return 0.0;
    float perp = abs(r.y);
    if (perp > w) return 0.0;
    // Edge-bright: больше яркость у границы, темнее в центре (стеклянный вид).
    float edge_t = perp / w;
    return 0.4 + edge_t * 0.6;
}

// SDF гексагона: положительное значение = расстояние ВНЕ, отрицательное = ВНУТРИ.
float hex_sdf(vec2 p, float r) {
    vec2 q = abs(p);
    return max(q.x * 0.866 + q.y * 0.5, q.y) - r;
}

void fragment() {
    float growth = texture(TEXTURE, UV).r;
    if (growth < 0.005) {
        discard;
    } else {
        vec2 tex_size = vec2(textureSize(TEXTURE, 0));
        vec2 cell = floor(UV * tex_size) + 0.5;
        vec2 cell_uv_center = cell / tex_size;
        vec2 to_center = (UV - cell_uv_center) * tex_size;     // -0.5..0.5
        float dist = length(to_center);

        int stage;
        if (growth < 0.20) stage = 0;
        else if (growth < 0.51) stage = 1;
        else if (growth < 0.86) stage = 2;
        else if (growth < 0.99) stage = 3;
        else stage = 4;

        float intensity = 0.0;
        bool is_solid = false;

        if (stage == 0) {
            // Крошечная точка-зерно (≈ 6% клетки).
            float r = 0.06 * (0.4 + growth * 3.0);    // плавно подрастает
            intensity = smoothstep(r, 0.0, dist);
        }
        else if (stage == 1) {
            // Одиночный шард: 1 узкий длинный спир + крошечная точка-основание.
            float length = 0.32 * smoothstep(0.20, 0.50, growth);
            float angle = 0.55;   // фиксированный «наклон» — выглядит как осколок
            intensity = spike_intensity(to_center, angle, length, spike_width_base);
            // Базовая точка
            intensity = max(intensity, smoothstep(0.05, 0.0, dist));
        }
        else if (stage == 2) {
            // Малый кластер: 4 спира под углами 0/90/180/270 + центральная точка.
            float length = 0.40 * smoothstep(0.50, 0.85, growth);
            for (int i = 0; i < 4; i++) {
                float a = float(i) * 1.5707963;   // π/2
                intensity = max(intensity, spike_intensity(to_center, a, length, spike_width_base * 1.1));
            }
            intensity = max(intensity, smoothstep(0.07, 0.0, dist));
        }
        else if (stage == 3) {
            // Большой кластер: 6 спиров.
            float length = 0.48;
            for (int i = 0; i < 6; i++) {
                float a = float(i) * 1.0471976;   // π/3
                intensity = max(intensity, spike_intensity(to_center, a, length, spike_width_base * 1.3));
            }
            // Центральная сердцевина — небольшой шестиугольник.
            float core = -hex_sdf(to_center, 0.10);
            if (core > 0.0) intensity = max(intensity, smoothstep(0.0, 0.05, core));
        }
        else {
            // Stage 4: ЗРЕЛЫЙ БЛОК — большой шестигранник + фасеточные грани.
            float core = -hex_sdf(to_center, 0.34);
            if (core > -0.04) {
                is_solid = true;
                // Внутри: ярко в центре, темнее на краях (фасет-эффект).
                float t = clamp(core / 0.30, 0.0, 1.0);
                intensity = mix(0.55, 1.0, t);
            } else {
                // Снаружи (полоса до 0.04 от грани): едва видный glow halo.
                float halo = smoothstep(0.10, 0.0, -core);
                intensity = max(intensity, halo * 0.30);
            }
            // Внешние шипы из углов (6 шт), короче для зрелого вида.
            float length = 0.20;
            for (int i = 0; i < 6; i++) {
                float a = float(i) * 1.0471976 + 0.5236;
                intensity = max(intensity, spike_intensity(to_center, a, length, spike_width_base * 0.9));
            }
        }

        if (intensity < 0.01) {
            discard;
        } else {
            // Цвет: глубина → яркая фасет → белый блик у вершин/центра.
            vec3 col = mix(c_dark, c_bright, intensity);
            if (intensity > 0.75) {
                col = mix(col, c_white, (intensity - 0.75) * 4.0);
            }

            // Лёгкая пульсация (зрелые пульсируют сильнее).
            float pulse_amp = stage == 4 ? 0.12 : 0.18;
            float pulse = 1.0 + pulse_amp * sin(TIME * pulse_speed + cell.x * 0.7 + cell.y * 1.3);
            col *= pulse;

            float alpha = is_solid ? 1.0 : intensity;
            COLOR = vec4(col, alpha);
        }
    }
}
