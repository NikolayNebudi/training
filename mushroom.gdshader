shader_type canvas_item;
render_mode blend_add;

// Per-instance quad. Цвет приходит через INSTANCE_CUSTOM.rgb, фаза
// пульсации — через INSTANCE_CUSTOM.a (0..1, разная для каждого гриба).
//
// Геометрия: тело (маленькая яркая точка) + широкое мягкое гало.
// Всё в локальных UV квада (0..1), радиальное расстояние от центра 0..1.
// Это автоматически круглое — никакого квадратного клиппинга.

uniform float body_radius : hint_range(0.0, 0.5) = 0.06;
uniform float halo_radius : hint_range(0.05, 0.5) = 0.45;
uniform float intensity   : hint_range(0.0, 5.0) = 1.4;
uniform float pulse_speed : hint_range(0.0, 5.0) = 1.0;

varying vec4 v_custom;

void vertex() {
    v_custom = INSTANCE_CUSTOM;
}

void fragment() {
    vec2 d = (UV - 0.5) * 2.0;       // -1..1 по оси
    float dist = length(d);          // 0..1.41 (углы квада)

    if (dist > 1.0) {
        discard;
    } else {
        vec3 col = v_custom.rgb;
        float phase = v_custom.a * 6.28318 + TIME * pulse_speed;
        float pulse = 0.85 + 0.15 * sin(phase);

        // Маленькое яркое тело-«шляпка» в центре.
        float body = smoothstep(body_radius, 0.0, dist) * 1.6;
        // Гало: плавный переход от body до halo_radius. Дальше — затухание
        // до 0 на краю квада.
        float halo_inner = body_radius;
        float halo_outer = halo_radius;
        float halo = smoothstep(halo_outer, halo_inner, dist) * 0.45;
        // Ультра-мягкое затухание от halo_outer до 1.0 (края квада).
        float fade = smoothstep(1.0, halo_outer, dist) * 0.10;
        halo += fade;

        float total = (body + halo) * intensity * pulse;
        COLOR = vec4(col * total, total);
    }
}
