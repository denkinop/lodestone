export const vertexSource = `#version 300 es
layout(location = 0) in vec3 position;
layout(location = 1) in vec3 normal;
layout(location = 2) in vec2 uv;
layout(location = 3) in vec4 binormal;
uniform mat4 viewProjection;
uniform bool aimEyes;
uniform vec4 eyePlane;
uniform vec3 eyeCenters[2];
uniform vec4 eyeRotation;
out vec3 vPosition;
out vec3 vNormal;
out vec2 vUv;
out vec4 vBinormal;

vec3 rotateEye(vec3 value, vec4 rotation) {
    // Rodrigues' formula with the unnormalized cross product of the two gaze axes.
    vec3 perpendicular = cross(rotation.xyz, value);
    return value + perpendicular + rotation.w * cross(rotation.xyz, perpendicular);
}

void main() {
    vPosition = position;
    vNormal = normal;
    vUv = uv;
    vBinormal = binormal;
    if (aimEyes) {
        int side = dot(eyePlane, vec4(position, 1.0)) > 0.0 ? 0 : 1;
        vec3 center = eyeCenters[side];
        vPosition = center + rotateEye(position - center, eyeRotation);
        vNormal = rotateEye(normal, eyeRotation);
        vBinormal.xyz = rotateEye(binormal.xyz, eyeRotation);
    }
    gl_Position = viewProjection * vec4(vPosition, 1.0);
}`;

const textureInputs = `
uniform int surface;
uniform bool isFace;
uniform float alphaCutoff;
uniform bool hasBaseMap;
uniform bool hasNormalMap;
uniform sampler2D baseMap;
uniform sampler2D normalMap;

float opacity(vec4 base, vec4 normalTex) {
    if (surface == 2 || surface == 4 || surface == 7) return hasNormalMap ? normalTex.a : base.a;
    if (surface == 3) return 1.0;
    if (surface == 5 || surface == 6) return hasNormalMap ? normalTex.b : base.a;
    return base.a;
}

float cutoutThreshold() {
    return surface == 2 ? clamp(alphaCutoff, 0.35, 0.86) : clamp(alphaCutoff, 0.30, 0.96);
}`;

export const shadowSource = `#version 300 es
precision highp float;
in vec2 vUv;
${textureInputs}
void main() {
    if (opacity(texture(baseMap, vUv), texture(normalMap, vUv)) < cutoutThreshold()) discard;
}`;

export const fragmentSource = `#version 300 es
precision highp float;
precision highp sampler2DShadow;
in vec3 vPosition;
in vec3 vNormal;
in vec2 vUv;
in vec4 vBinormal;
out vec4 pixel;
${textureInputs}
uniform vec3 camera;
uniform vec3 lightDirection;
uniform vec3 tint;
uniform vec3 accent;
uniform vec4 eyePlane;
uniform vec3 hairSheen;
uniform vec4 lips;
uniform vec3 diffuseTint;
uniform vec3 specularTint;
uniform vec3 scleraTint;
uniform vec3 eyeReflection;
uniform vec3 eyeEmission;
uniform vec3 dye1;
uniform vec3 dye2;
uniform vec4 diffuseRows[32];
uniform vec4 specularRows[32];
uniform vec4 emissionRows[32];
uniform int colorRows;
uniform bool neutralLight;
uniform bool portraitView;
uniform bool hairEdgePass;
uniform bool hasIndexMap;
uniform bool hasMaskMap;
uniform bool hasSpecularMap;
uniform sampler2D maskMap;
uniform sampler2D indexMap;
uniform sampler2D specularMap;
uniform sampler2DShadow shadowMap;
uniform mat4 lightProjection;
uniform float shadowTexel;
const float PI = 3.14159265;

vec3 linearize(vec3 value) {
    return mix(value / 12.92, pow((value + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), value));
}

vec3 encodeSrgb(vec3 value) {
    return mix(value * 12.92, 1.055 * pow(value, vec3(1.0 / 2.4)) - 0.055,
        step(vec3(0.0031308), value));
}

mat3 surfaceFrame(vec3 n) {
    vec3 b = vBinormal.xyz - n * dot(n, vBinormal.xyz);
    if (dot(b, b) > 0.05) {
        b = normalize(b);
        return mat3(normalize(cross(b, n)) * sign(vBinormal.w), b, n);
    }
    vec3 dx = dFdx(vPosition);
    vec3 dy = dFdy(vPosition);
    vec2 ux = dFdx(vUv);
    vec2 uy = dFdy(vUv);
    float orientation = sign(ux.x * uy.y - uy.x * ux.y);
    vec3 t = (dx * uy.y - dy * ux.y) * orientation;
    vec3 nextB = (dy * ux.x - dx * uy.x) * orientation;
    float size = max(max(dot(t, t), dot(nextB, nextB)), 0.000000001);
    return mat3(t * inversesqrt(size), nextB * inversesqrt(size), n);
}

float visibility(vec3 n, vec3 l) {
    vec3 uvz = (lightProjection * vec4(vPosition, 1.0)).xyz * 0.5 + 0.5;
    if (any(lessThan(uvz, vec3(0.0))) || any(greaterThan(uvz, vec3(1.0)))) return 1.0;
    uvz.z -= mix(0.0021, 0.0007, max(dot(n, l), 0.0));
    float lit = 0.0;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            lit += texture(shadowMap, uvz + vec3(vec2(float(x), float(y)) * shadowTexel * 1.8, 0.0));
        }
    }
    return lit / 9.0;
}

vec3 directLight(vec3 n, vec3 view, vec3 light, vec3 energy,
        vec3 diffuse, vec3 f0, float rough, float thickness, float shadow) {
    vec3 halfVector = normalize(view + light);
    float nv = max(dot(n, view), 0.001);
    float nl = max(dot(n, light), 0.0);
    float nh = max(dot(n, halfVector), 0.0);
    float vh = max(dot(view, halfVector), 0.0);
    float a2 = rough * rough * rough * rough;
    float shape = nh * nh * (a2 - 1.0) + 1.0;
    float distribution = a2 / max(PI * shape * shape, 0.000002);
    float v = nl * sqrt(a2 + nv * nv * (1.0 - a2));
    float l = nv * sqrt(a2 + nl * nl * (1.0 - a2));
    vec3 fresnel = f0 + (1.0 - f0) * pow(1.0 - vh, 5.0);
    vec3 specular = fresnel * min(distribution * 0.5 / max(v + l, 0.001), 4.5);
    // A small wrap term lets light reach thin skin and hair near the terminator.
    float wrap = clamp((dot(n, light) + 0.3) / 1.3, 0.0, 1.0);
    return energy * shadow * (diffuse * mix(nl, wrap, thickness) / PI + specular * nl);
}

float reflectionSpot(vec3 ray, vec3 light, float sharpness) {
    float angle = 1.0 - dot(ray, normalize(light));
    return exp(-max(angle, 0.0) * sharpness);
}

vec3 eyeSurface(vec4 base, vec4 mask, vec3 geometric, vec3 normal, vec3 view) {
    vec3 textureColor = linearize(base.rgb);
    vec3 tintColor = dot(eyePlane, vec4(vPosition, 1.0)) > 0.0 ? accent : tint;
    float iris = smoothstep(0.04, 0.95, mask.b);
    // Eyelids darken the upper iris while the radial texture remains visible below.
    float lid = smoothstep(0.43, 0.71, vUv.y);
    vec3 pigment = mix(scleraTint * 0.73, tintColor * 0.80, iris);
    vec3 diffuse = textureColor * pigment * diffuseTint;
    diffuse *= mix(0.14, 0.77, lid) + 0.04 * max(dot(geometric, lightDirection), 0.0);
    diffuse += textureColor * iris * vec3(0.004, 0.005, 0.006);

    // A smooth cornea reflects the environment separately from the textured iris.
    vec3 cornea = normalize(mix(geometric, normal, 0.10));
    vec3 ray = reflect(-view, cornea);
    float upper = reflectionSpot(ray, vec3(-0.16, 0.29, 0.94), 190.0);
    float next = reflectionSpot(ray, vec3(0.14, 0.20, 0.97), 250.0);
    float pin = reflectionSpot(ray, vec3(0.36, 0.30, 0.89), 280.0);
    float sky = reflectionSpot(ray, vec3(-0.42, 0.74, 0.53), 12.0);
    float grazing = pow(1.0 - clamp(dot(geometric, view), 0.0, 1.0), 3.0);
    float strength = smoothstep(0.08, 0.36, mask.g) * mix(0.28, 1.0, iris);
    vec3 envColor = portraitView && !neutralLight ? vec3(1.0, 0.91, 0.78) : vec3(0.86, 0.94, 1);
    vec3 shine = envColor * mix(vec3(0.54), eyeReflection, 0.55)
        * (upper * 0.66 + next * 0.35 + pin * 0.43 + sky * 0.021 + grazing * 0.028);
    return diffuse + strength * shine + eyeEmission * mask.r;
}

void main() {
    vec4 base = texture(baseMap, vUv);
    vec4 normalTex = texture(normalMap, vUv);
    vec4 mask = texture(maskMap, vUv);
    vec4 indexValue = texture(indexMap, vUv);
    float alpha = opacity(base, normalTex);
    float coverage = 1.0;
    if (surface == 2) {
        // Widen the opacity transition when a strand spans only a few pixels.
        float threshold = cutoutThreshold();
        float width = clamp(fwidth(alpha) * 0.65, 0.095, 0.22);
        coverage = smoothstep(threshold - width, min(0.999, threshold + width), alpha);
        bool solid = coverage >= 0.98;
        if (hairEdgePass ? solid || coverage < 0.015 : !solid) discard;
    } else if (alpha < (surface == 7 ? 0.065 : cutoutThreshold())) {
        discard;
    }
    if (surface == 7) {
        // The face atlas packs lashes at the top and brows below them.
        float lash = 1.0 - smoothstep(0.143, 0.185, vUv.y);
        vec3 brow = mix(vec3(0.030, 0.020, 0.015), tint * 0.21, 0.58);
        vec3 hair = mix(brow, vec3(0.011, 0.008, 0.008), lash);
        pixel = vec4(encodeSrgb(hair), smoothstep(0.055, 0.82, alpha));
        return;
    }

    vec3 diffuse = hasBaseMap ? linearize(base.rgb) : vec3(1.0);
    vec3 specular = vec3(0.038);
    vec3 emissive = vec3(0.0);
    float rough = 0.66;
    float metalness = 0.0;
    float ao = 1.0;
    float thickness = 0.0;

    if (surface == 1) {
        diffuse *= mix(vec3(1.0), tint, hasNormalMap ? normalTex.b : 1.0);
        if (isFace) diffuse = mix(diffuse, lips.rgb, normalTex.a * lips.a);
        if (!neutralLight && !portraitView) diffuse *= vec3(0.91, 0.99, 1.13);
        specular *= mix(0.55, 1.8, mask.r);
        rough = mix(0.37, 0.82, mask.g);
        thickness = 0.38 * mask.b;
    } else if (surface == 2) {
        diffuse *= mix(tint, accent, normalTex.b);
        if (!neutralLight && !portraitView) diffuse *= vec3(0.92, 0.98, 1.12);
        diffuse *= mix(0.55, 1.05, mask.a * mask.a);
        ao = mix(0.41, 1.0, mask.a);
        specular = vec3(mix(0.012, 0.040, mask.r));
        rough = mix(0.35, 0.72, mask.g);
        rough = mix(max(rough, 0.62), rough, coverage);
        thickness = 0.18 * mask.b;
    } else if (surface == 4) {
        diffuse = tint * 0.48;
        specular *= 0.38;
    } else if (colorRows > 0) {
        int row;
        float blend;
        if (colorRows == 32) {
            row = 2 * int(clamp(floor(indexValue.r * 15.0 + 0.5), 0.0, 15.0));
            blend = 1.0 - indexValue.g;
        } else {
            row = int(clamp(floor(normalTex.a * 15.0 + 0.5), 0.0, 15.0));
            blend = 0.0;
        }
        int nextRow = min(row + 1, colorRows - 1);
        vec4 d = mix(diffuseRows[row], diffuseRows[nextRow], blend);
        vec4 s = mix(specularRows[row], specularRows[nextRow], blend);
        vec4 e = mix(emissionRows[row], emissionRows[nextRow], blend);
        bool legacy = surface == 5;
        float gloss = legacy ? 1.0 - mask.g : mask.g;
        ao = hasMaskMap && e.a > 0.5 ? mix(0.24, 1.0, mask.b) : 1.0;
        metalness = legacy ? (1.0 - ao) * 0.78 : s.a;
        rough = mix(d.a, gloss, hasMaskMap ? 0.62 : 0.0);
        vec3 specMask = hasSpecularMap ? linearize(texture(specularMap, vUv).rgb)
            : vec3(hasMaskMap ? mask.r : 0.55);
        diffuse *= d.rgb;
        vec3 rowSpecular = s.rgb * specMask;
        specular = mix(clamp(rowSpecular * 0.085, vec3(0.006), vec3(0.11)),
            clamp(rowSpecular * 0.39, vec3(0.012), vec3(0.66)), metalness);
        emissive = e.rgb * 0.38;
        if (dye1.r >= 0.0) diffuse = mix(diffuse, dye1 * (0.34 + diffuse * 0.66), 0.46);
        if (dye2.r >= 0.0 && hasIndexMap) diffuse = mix(diffuse, dye2, indexValue.g * 0.50);
    } else if (!hasBaseMap) {
        diffuse = vec3(0.33);
    }
    diffuse *= diffuseTint;
    specular *= specularTint;
    rough = clamp(rough, 0.15, 0.97);

    vec3 view = normalize(camera - vPosition);
    vec3 n = normalize(vNormal);
    if (dot(n, view) < 0.0) n = -n;
    vec3 geometric = n;
    mat3 frame = surfaceFrame(n);
    if (hasNormalMap) {
        vec2 xy = normalTex.xy * 2.0 - 1.0;
        xy *= surface == 1 ? 0.70 : 1.0;
        n = normalize(frame * vec3(xy, sqrt(max(0.04, 1.0 - dot(xy, xy)))));
    }
    if (surface == 3) {
        vec3 eye = eyeSurface(base, mask, geometric, n, view);
        pixel = vec4(encodeSrgb(max(eye / (1.0 + eye * 0.25), vec3(0.0))), 1.0);
        return;
    }
    vec3 reflected = reflect(-view, n);
    vec3 f0 = mix(specular, max(specular, diffuse * 0.7), metalness);
    vec3 matte = diffuse * (1.0 - 0.86 * metalness);
    vec3 floorLight = neutralLight ? vec3(0.16) : vec3(0.092, 0.113, 0.18);
    vec3 skyLight = neutralLight ? vec3(0.39) : vec3(0.28, 0.35, 0.54);
    if (!neutralLight && portraitView) {
        floorLight = vec3(0.18, 0.146, 0.118);
        skyLight = vec3(0.38, 0.345, 0.30);
    }
    vec3 ambient = mix(floorLight, skyLight, n.y * 0.5 + 0.5) * matte * ao;
    vec3 environment = mix(floorLight, skyLight, reflected.y * 0.5 + 0.5);
    vec3 reflectLight = environment * f0 * mix(1.35, 0.24, rough) * ao;
    float shadowFloor = surface == 1 ? (portraitView ? 0.56 : 0.43) : 0.27;
    float shadow = mix(shadowFloor, 1.0, visibility(geometric, lightDirection));
    vec3 key = neutralLight ? vec3(2.55) : vec3(2.33, 2.51, 3.02);
    vec3 fill = neutralLight ? vec3(0.68) : vec3(0.69, 0.80, 1.22);
    vec3 rim = neutralLight ? vec3(0.72) : vec3(0.62, 0.84, 1.55);
    if (!neutralLight && portraitView) {
        key = vec3(2.29, 2.14, 1.96);
        fill = vec3(0.91, 0.82, 0.70);
        rim = vec3(0.65, 0.60, 0.54);
    }
    vec3 light = ambient + reflectLight + emissive;
    light += directLight(n, view, lightDirection, key, matte, f0, rough, thickness, shadow);
    light += directLight(n, view, normalize(vec3(-0.75, 0.35, 0.78)), fill, matte, f0, rough, thickness, 1.0);
    light += directLight(n, view, normalize(vec3(-0.44, 0.48, -0.77)), rim, matte, f0, rough, thickness, 1.0);
    if (surface == 2) {
        // Hair's specular band follows the strand direction instead of an isotropic point highlight.
        vec3 h = normalize(view + lightDirection);
        float sine = sqrt(max(0.0, 1.0 - pow(clamp(dot(normalize(frame[1]), h), -1.0, 1.0), 2.0)));
        light += key * pow(sine, 28.0) * max(dot(geometric, lightDirection), 0.0)
            * shadow * mix(0.006, 0.057, mask.r) * mix(vec3(0.16), hairSheen, 0.84)
            * mix(0.3, 1.0, coverage);
        // Approximate hair absorption in the two lighting presets.
        light *= neutralLight ? vec3(0.47) : portraitView ? vec3(0.49, 0.48, 0.46) : vec3(0.35, 0.38, 0.51);
    }
    if (surface == 1 && portraitView && !neutralLight) {
        light *= vec3(0.68, 0.83, 0.99);
    }
    light = max(light, vec3(0.0));
    pixel = vec4(encodeSrgb(light / (1.0 + light * 0.25)), hairEdgePass ? coverage : 1.0);
}`;
