shader_type canvas_item;

// Sprite-носитель — Sprite2D с density-текстурой 800×800 (L8), растянутой на
// всю карту. Дополнительно используем wetness-текстуру (биом мха) и
// obstacle-текстуру (1 = камень/стена «возвышение», 0 = пол «низина»).
//
// Принципы:
//   - Domain warping + edge erosion → органичная форма с рваными мелкими краями.
//   - 3 высокочастотных слоя нойза → крапчатая текстура внутри пятна.
//   - Elevation tint по obstacle: мох на камне ярче и зеленее (он наверху),
//     мох на полу темнее и теплее (в тени). На границе rock/floor это
//     создаёт визуальный «уступ» — две моховые «биоты» больше не сливаются.
//   - Матовое освещение: стандартное 2D-смешение, без overdrive.

uniform sampler2D wetness_texture  : filter_linear;
uniform sampler2D obstacle_texture : filter_linear;

uniform float density_threshold : hint_range(0.0, 1.0) = 0.10;
uniform float edge_erosion      : hint_range(0.0, 1.0) = 0.55;
uniform float erosion_scale     : hint_range(50.0, 4000.0) = 1100.0;

uniform float warp_scale_a   : hint_range(1.0, 400.0) = 55.0;
uniform float warp_scale_b   : hint_range(1.0, 800.0) = 160.0;
uniform float warp_strength  : hint_range(0.0, 0.10) = 0.020;

uniform float speckle_scale_a   : hint_range(200.0, 4000.0)  = 1100.0;
uniform float speckle_scale_b   : hint_range(500.0, 6000.0)  = 2400.0;
uniform float speckle_scale_c   : hint_range(1000.0, 10000.0) = 4800.0;
uniform float speckle_strength  : hint_range(0.0, 1.5)        = 0.85;

// Биомы — приглушённые оливково-болотные тона.
uniform vec4 moss_dry_dark    : source_color = vec4(0.05, 0.06, 0.02, 1.0);
uniform vec4 moss_dry_bright  : source_color = vec4(0.14, 0.13, 0.05, 1.0);
uniform vec4 moss_med_dark    : source_color = vec4(0.04, 0.09, 0.03, 1.0);
uniform vec4 moss_med_bright  : source_color = vec4(0.10, 0.18, 0.05, 1.0);
uniform vec4 moss_wet_dark    : source_color = vec4(0.03, 0.10, 0.07, 1.0);
uniform vec4 moss_wet_bright  : source_color = vec4(0.07, 0.18, 0.13, 1.0);

// Elevation: «низина vs возвышение».
uniform float floor_dim         : hint_range(0.2, 1.0) = 0.55;          // 0 = пол совсем тёмный
uniform vec3  rock_moss_tint    : source_color = vec3(0.92, 1.05, 0.92); // мох на камне холоднее/зеленее
uniform vec3  floor_moss_tint   : source_color = vec3(1.10, 0.95, 0.82); // мох на полу теплее
uniform float boundary_shadow   : hint_range(0.0, 1.0) = 0.40;          // тёмная «трещина» на стыке

float hash21(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float vnoise(vec2 uv) {
    vec2 i = floor(uv);
    vec2 f = fract(uv);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash21(i);
    float b = hash21(i + vec2(1.0, 0.0));
    float c = hash21(i + vec2(0.0, 1.0));
    float d = hash21(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm4(vec2 p) {
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++) {
        v += a * vnoise(p);
        p = p * 2.13 + vec2(13.7, 21.1);
        a *= 0.5;
    }
    return v;
}

void fragment() {
    // Domain warping для «закрученных», не круглых форм.
    vec2 q = vec2(fbm4(UV * warp_scale_a),
                  fbm4(UV * warp_scale_a + vec2(11.4, 27.1)));
    vec2 r = vec2(fbm4(UV * warp_scale_b + q),
                  fbm4(UV * warp_scale_b + q + vec2(43.0, 67.0)));
    vec2 sample_uv = UV + (r - 0.5) * warp_strength;

    float density  = texture(TEXTURE, sample_uv).r;
    float wetness  = texture(wetness_texture, UV).r;
    float on_rock  = texture(obstacle_texture, UV).r;

    if (density < density_threshold) {
        discard;
    } else {
        // Мелкие рваные края: высокочастотный fBm пробивает дыры у границы.
        float edge_dist = density - density_threshold;
        float erode = fbm4(UV * erosion_scale);
        float in_band = step(edge_dist, edge_erosion * 0.30);
        float erode_chance = clamp(1.0 - edge_dist / (edge_erosion * 0.30 + 0.0001), 0.0, 1.0);
        if (in_band > 0.5 && erode > 1.0 - erode_chance * 0.55) {
            discard;
        } else {
            // Биом плавно по wetness.
            float t_med = smoothstep(0.30, 0.50, wetness);
            float t_wet = smoothstep(0.60, 0.80, wetness);
            vec3 dark = mix(
                mix(moss_dry_dark.rgb, moss_med_dark.rgb, t_med),
                moss_wet_dark.rgb, t_wet);
            vec3 bright = mix(
                mix(moss_dry_bright.rgb, moss_med_bright.rgb, t_med),
                moss_wet_bright.rgb, t_wet);

            float color_t = smoothstep(density_threshold, 1.0, density);
            vec3 col = mix(dark, bright, color_t);

            // Крапчатая текстура — мелкие тёмные впадины и светлые точки.
            float speckle_a = vnoise(UV * speckle_scale_a);
            float speckle_b = vnoise(UV * speckle_scale_b);
            float speckle_c = vnoise(UV * speckle_scale_c);
            col *= mix(1.0, 0.55, speckle_a * speckle_strength);
            col *= mix(0.80, 1.20, speckle_b);
            col += bright * 0.18 * smoothstep(0.65, 1.0, speckle_c) * speckle_strength;

            // ELEVATION: мох на камне ярче и зеленее, на полу темнее и теплее.
            // on_rock плавно интерполируется (linear filter) → у границы получается
            // 0.5, что даёт «трещину» благодаря boundary_shadow ниже.
            vec3 substrate_tint = mix(floor_moss_tint, rock_moss_tint, on_rock);
            float elevation_brightness = mix(floor_dim, 1.0, on_rock);
            col *= substrate_tint * elevation_brightness;

            // Тёмная «трещина» на самой границе rock/floor: где on_rock ≈ 0.5,
            // shadow_band ≈ 1, мох темнеет. Результат — видимый перепад уровней.
            float shadow_band = 1.0 - 4.0 * abs(on_rock - 0.5) * abs(on_rock - 0.5);
            shadow_band = clamp(shadow_band, 0.0, 1.0);
            col *= 1.0 - shadow_band * boundary_shadow;

            float alpha = smoothstep(density_threshold, density_threshold + 0.05, density);
            COLOR = vec4(col, alpha);
        }
    }
}

void light() {
    // Матовая реакция на свет: стандартное 2D-смешение, никакого overdrive.
    LIGHT = vec4(COLOR.rgb * LIGHT_COLOR.rgb, COLOR.a);
}
