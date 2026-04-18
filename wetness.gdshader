shader_type canvas_item;
render_mode blend_mul;

// Спрайт-носитель — Sprite2D с wetness-текстурой 800×800 (L8), растянутой на
// всю карту. blend_mul: каждый пиксель умножает то, что уже отрисовано ниже
// (пол + камень). Множитель 1.0 = без изменений, ниже = темнее.
//
// Эффект сильнее проявляется в ОСВЕЩЁННЫХ зонах (свет * 0.7 = тёмная
// разница; темнота * 0.7 = почти ничего). Это естественно ограничивает
// видимость мокрой поверхности зоной факела игрока.
//
// Никакой анимированной ряби и блёсток — мокрая земля статична.

uniform float wet_threshold   : hint_range(0.0, 1.0)   = 0.30;
uniform float wet_full        : hint_range(0.0, 1.0)   = 0.85;
uniform float darken_strength : hint_range(0.0, 1.0)   = 0.32;
uniform vec3  wet_tint        : source_color           = vec3(0.78, 0.84, 0.95);
uniform float tint_strength   : hint_range(0.0, 1.0)   = 0.25;

void fragment() {
    float wetness = texture(TEXTURE, UV).r;
    float t = smoothstep(wet_threshold, wet_full, wetness);

    float darken = 1.0 - t * darken_strength;
    vec3 tint = mix(vec3(1.0), wet_tint, t * tint_strength);
    vec3 mod_color = tint * darken;

    COLOR = vec4(mod_color, 1.0);
}
