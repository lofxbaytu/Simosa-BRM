// 海面著色器(規劃書第 7.3 節):quality 0 = 低(平面 + 法線貼圖捲動,無頂點位移、無泡沫);
// 1/2 = 中/高(Gerstner 波頂點位移 2–3 組 + 波峰泡沫)。Forward+ 與 Compatibility 皆可編譯(只用基本功能)。
shader_type spatial;
render_mode cull_back, depth_draw_opaque, specular_schlick_ggx;

uniform int quality = 0;
uniform float time_s = 0.0;
uniform float wave_height = 0.5;      // 有義波高(m)
uniform vec2 wave_dir = vec2(0.0, 1.0); // 浪的去向(場景 x、z)
uniform vec3 deep_color : source_color = vec3(0.02, 0.09, 0.16);
uniform vec3 shallow_color : source_color = vec3(0.05, 0.25, 0.30);
uniform vec3 foam_color : source_color = vec3(0.85, 0.9, 0.9);
uniform float daylight = 1.0;
uniform sampler2D normal_map : hint_normal;
uniform float normal_scale = 0.04;   // 法線貼圖重複尺度(1/m)
uniform float roughness_value = 0.12;

varying float v_crest;

// Gerstner 波:位移 (x, y, z) 與法線擾動
vec3 gerstner(vec2 dir, float amp, float wavelength, float steep, float speed, vec2 pos, inout vec3 normal) {
    float k = 6.28318 / wavelength;
    float c = sqrt(9.81 / k) * speed;
    vec2 d = normalize(dir);
    float f = k * (dot(d, pos) - c * time_s);
    float a = steep / k * amp;
    normal.x -= d.x * amp * k * cos(f);
    normal.z -= d.y * amp * k * cos(f);
    return vec3(d.x * a * cos(f), amp * sin(f), d.y * a * cos(f));
}

void vertex() {
    vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
    v_crest = 0.0;
    if (quality > 0 && wave_height > 0.01) {
        vec3 n = vec3(0.0, 1.0, 0.0);
        vec2 p = world.xz;
        vec3 disp = gerstner(wave_dir, wave_height * 0.5, 40.0 + wave_height * 30.0, 0.6, 1.0, p, n);
        disp += gerstner(wave_dir + vec2(0.35, -0.2), wave_height * 0.25, 17.0, 0.5, 1.1, p, n);
        if (quality > 1) {
            disp += gerstner(wave_dir + vec2(-0.5, 0.3), wave_height * 0.12, 7.0, 0.4, 1.3, p, n);
        }
        VERTEX += disp;
        NORMAL = normalize(n);
        v_crest = clamp(disp.y / max(wave_height * 0.6, 0.05), 0.0, 1.0);
    }
}

void fragment() {
    vec3 world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
    vec2 uv1 = world.xz * normal_scale + wave_dir * time_s * 0.02;
    vec2 uv2 = world.xz * normal_scale * 2.3 - wave_dir * time_s * 0.015;
    vec3 n1 = texture(normal_map, uv1).rgb * 2.0 - 1.0;
    vec3 n2 = texture(normal_map, uv2).rgb * 2.0 - 1.0;
    vec3 nm = normalize(vec3(n1.xy + n2.xy, n1.z * n2.z));
    NORMAL_MAP = nm * 0.5 + 0.5;
    NORMAL_MAP_DEPTH = quality > 0 ? 1.0 : 0.6;
    float fres = pow(1.0 - clamp(dot(normalize(NORMAL), normalize(VIEW)), 0.0, 1.0), 3.0);
    vec3 col = mix(deep_color, shallow_color, fres * 0.6);
    if (quality > 0) {
        float foam = smoothstep(0.75, 1.0, v_crest) * clamp(wave_height - 0.3, 0.0, 1.0);
        col = mix(col, foam_color, foam * 0.8);
    }
    ALBEDO = col * (0.25 + 0.75 * daylight);
    ROUGHNESS = roughness_value;
    SPECULAR = 0.5;
    METALLIC = 0.0;
}
