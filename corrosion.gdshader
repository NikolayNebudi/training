shader_type canvas_item;
render_mode blend_mul;

// Спрайт-носитель — Sprite2D с corrosion-текстурой 800×800 (L8). Значение
// в пикселе = «насколько изношена эта клетка». Шейдер ветвится по
// obstacle_texture (1 = камень, 0 = пол) и применяет соответствующий стиль:
//
//   Камень → ржавчина: рыжевато-коричневый тинт, сильное затемнение, рваный
//                       высокочастотный шум (как окалина).
//   Пол    → грязь/износ: тусклый серо-коричневый тинт, мягкое затемнение,
//                          крупный шум для «потрёпанности» поверхности.
//
// Это разнообразит и пол, и камни одним общим механизмом.

uniform sampler2D obstacle_texture : filter_linear;

// Камень
uniform vec3  rock_corrosion_tint : source_color = vec3(0.78, 0.70, 0.55);
uniform float rock_darken         : hint_range(0.0, 1.0) = 0.40;
uniform float rock_tint_strength  : hint_range(0.0, 1.0) = 0.55;
uniform float rock_noise_scale    : hint_range(50.0, 5000.0) = 950.0;

// Пол
uniform vec3  floor_wear_tint     : source_color = vec3(0.55, 0.50, 0.42);
uniform float floor_darken        : hint_range(0.0, 1.0) = 0.55;
uniform float floor_tint_strength : hint_range(0.0, 1.0) = 0.65;
uniform float floor_noise_scale   : hint_range(50.0, 3000.0) = 280.0;

uniform float noise_strength : hint_range(0.0, 1.0) = 0.55;

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

void fragment() {
    float wear = texture(TEXTURE, UV).r;
    if (wear < 0.01) {
        discard;
    } else {
        float on_rock = texture(obstacle_texture, UV).r;

        // Параметры выбираются плавно — на границе rock/floor (linear filter
        // даёт on_rock ≈ 0.5) эффекты микшируются.
        vec3  tint_color   = mix(floor_wear_tint, rock_corrosion_tint, on_rock);
        float darken_amt   = mix(floor_darken, rock_darken, on_rock);
        float tint_str     = mix(floor_tint_strength, rock_tint_strength, on_rock);
        float noise_scale  = mix(floor_noise_scale, rock_noise_scale, on_rock);

        // Внутренняя «выветренная» текстура: высокочастотный нойз модулирует
        // силу эффекта, чтобы выветривание не было плоским.
        float n = vnoise(UV * noise_scale);
        float t = wear * mix(1.0 - noise_strength * 0.5, 1.0 + noise_strength * 0.3, n);
        t = clamp(t, 0.0, 1.0);

        float darken = 1.0 - t * darken_amt;
        vec3 tint = mix(vec3(1.0), tint_color, t * tint_str);
        vec3 mod_color = tint * darken;

        COLOR = vec4(mod_color, 1.0);
    }
}
