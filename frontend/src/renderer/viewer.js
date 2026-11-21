import {vertexSource, fragmentSource, shadowSource} from "./shaders.js";

const WEAPONS = new Set(["main_hand", "off_hand"]);
const MAPS = ["base", "normal", "mask", "index", "specular"];
const SURFACES = {"skin.shpk": 1, "hair.shpk": 2, "iris.shpk": 3, "charactertattoo.shpk": 4,
    "characterlegacy.shpk": 5, "character.shpk": 6, "characterglass.shpk": 6,
    "characterscroll.shpk": 6, "characterinc.shpk": 6, "charactertransparency.shpk": 6};
const VERTEX_SIZE = 12;
const SHADOW_SIZE = 1536;
const LIGHT = [0.62, 0.85, 0.88];
const LIGHT_DIRECTION = LIGHT.map((v) => v / Math.hypot(...LIGHT));

function facialHair(material) {
    return material.isFace && material.shader === "hair.shpk";
}

function hairTriangles(vertices, indices) {
    const count = indices.length / 3;
    const centers = new Float32Array(indices.length);
    const center = [0, 0, 0];
    for (let i = 0; i < indices.length; i += 3) {
        const a = indices[i] * VERTEX_SIZE;
        const b = indices[i + 1] * VERTEX_SIZE;
        const c = indices[i + 2] * VERTEX_SIZE;
        for (let axis = 0; axis < 3; axis++) {
            const coordinate = (vertices[a + axis] + vertices[b + axis] + vertices[c + axis]) / 3;
            centers[i + axis] = coordinate;
            center[axis] += coordinate;
        }
    }
    return {
        centers,
        center: center.map((coordinate) => coordinate / Math.max(1, count)),
        order: Array.from({length: count}, (_, i) => i),
        depth: new Float32Array(count),
        sorted: new Uint16Array(indices.length),
        orderKey: null,
    };
}

function projectedDepth(p, offset, direction) {
    return p[offset] * direction[0] + p[offset + 1] * direction[1] + p[offset + 2] * direction[2];
}

function gazeRotation(forward, direction) {
    const distance = Math.hypot(...direction);
    const [x, y, z] = direction.map((coordinate) => coordinate / distance);
    const length = Math.hypot(...forward);
    const [a, b, c] = forward.map((coordinate) => coordinate / length);
    const cosine = a * x + b * y + c * z;
    if (cosine <= 0.5) return [0, 0, 0, 0];
    return [b * z - c * y, c * x - a * z, a * y - b * x, 1 / (1 + cosine)];
}

function shader(gl, type, source) {
    const shader = gl.createShader(type);
    gl.shaderSource(shader, source);
    gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
        throw new Error(gl.getShaderInfoLog(shader));
    }
    return shader;
}

function program(gl, fragment) {
    const program = gl.createProgram();
    const vs = shader(gl, gl.VERTEX_SHADER, vertexSource);
    const fs = shader(gl, gl.FRAGMENT_SHADER, fragment);
    gl.attachShader(program, vs);
    gl.attachShader(program, fs);
    gl.linkProgram(program);
    gl.deleteShader(vs);
    gl.deleteShader(fs);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        throw new Error(gl.getProgramInfoLog(program));
    }
    return program;
}

function unpack(base64, Type) {
    const bytes = Uint8Array.from(atob(base64), (char) => char.charCodeAt(0));
    return new Type(bytes.buffer);
}

function color(hex, fallback) {
    if (!/^#[0-9a-f]{6}$/i.test(hex || "")) return fallback;
    return [1, 3, 5].map((i) => {
        const value = Number.parseInt(hex.slice(i, i + 2), 16) / 255;
        return value <= 0.04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4;
    });
}

function boundsOf(meshes) {
    const min = [Infinity, Infinity, Infinity];
    const max = [-Infinity, -Infinity, -Infinity];
    for (const {vertices, indices} of meshes) {
        for (const index of indices) {
            for (let i = 0; i < 3; i++) {
                const value = vertices[index * VERTEX_SIZE + i];
                min[i] = Math.min(min[i], value);
                max[i] = Math.max(max[i], value);
            }
        }
    }
    if (!meshes.length) return {min: [-0.5, 0, -0.5], max: [0.5, 1, 0.5]};
    return {min, max};
}

function cameraMatrix(eye, center, width, height, far, perspective = false) {
    const vector = eye.map((v, i) => v - center[i]);
    const distance = Math.hypot(...vector);
    const forward = vector.map((v) => v / distance);
    const sideLength = Math.hypot(forward[0], forward[2]);
    const right = [forward[2] / sideLength, 0, -forward[0] / sideLength];
    const up = [
        forward[1] * right[2], forward[2] * right[0] - forward[0] * right[2],
        -forward[1] * right[0],
    ];
    const dot = (row) => row.reduce((sum, v, i) => sum + v * eye[i], 0);
    if (perspective) {
        const near = 0.02;
        const sx = 2 * distance / width;
        const sy = 2 * distance / height;
        const sz = -(far + near) / (far - near);
        return new Float32Array([
            right[0] * sx, up[0] * sy, forward[0] * sz, -forward[0],
            right[1] * sx, up[1] * sy, forward[1] * sz, -forward[1],
            right[2] * sx, up[2] * sy, forward[2] * sz, -forward[2],
            -dot(right) * sx, -dot(up) * sy, -dot(forward) * sz - 2 * far * near / (far - near), dot(forward),
        ]);
    }
    const sx = 2 / width;
    const sy = 2 / height;
    const sz = -2 / far;
    return new Float32Array([
        right[0] * sx, up[0] * sy, forward[0] * sz, 0,
        right[1] * sx, up[1] * sy, forward[1] * sz, 0,
        right[2] * sx, up[2] * sy, forward[2] * sz, 0,
        -dot(right) * sx, -dot(up) * sy, -dot(forward) * sz - 1, 1,
    ]);
}

export class Viewer {
    constructor(canvas, platform = null) {
        this.canvas = canvas;
        this.platform = platform;
        const gl = canvas.getContext("webgl2", {alpha: true, antialias: true, preserveDrawingBuffer: true});
        if (!gl) throw new Error("当前设备没有可用的 WebGL 2");
        this.gl = gl;
        this.program = program(gl, fragmentSource);
        const materialUniforms = ["surface", "alphaCutoff", "baseMap", "normalMap", "hasBaseMap", "hasNormalMap"];
        this.uniforms = Object.fromEntries([
            ...materialUniforms, "viewProjection", "camera", "tint", "accent", "eyePlane", "hairSheen", "dye1", "dye2",
            "aimEyes", "eyeCenters[0]", "eyeRotation",
            "lips", "diffuseTint", "specularTint", "scleraTint", "eyeReflection", "eyeEmission",
            "isFace", "neutralLight", "portraitView", "hairEdgePass", "lightDirection",
            "diffuseRows[0]", "specularRows[0]", "emissionRows[0]", "colorRows",
            "maskMap", "indexMap", "specularMap", "hasMaskMap", "hasIndexMap", "hasSpecularMap",
            "shadowMap", "lightProjection", "shadowTexel",
        ].map((name) => [name, gl.getUniformLocation(this.program, name)]));
        gl.useProgram(this.program);
        gl.enable(gl.DEPTH_TEST);
        gl.clearColor(0, 0, 0, 0);
        MAPS.forEach((name, i) => gl.uniform1i(this.uniforms[name + "Map"], i));
        gl.uniform1i(this.uniforms.shadowMap, MAPS.length);
        gl.uniform3fv(this.uniforms.lightDirection, LIGHT_DIRECTION);
        gl.uniform1f(this.uniforms.shadowTexel, 1 / SHADOW_SIZE);
        this.fallback = [
            this.solid([255, 255, 255, 255]), this.solid([128, 128, 255, 255]),
            this.solid([128, 128, 255, 255]), this.solid([0, 255, 0, 255]),
            this.solid([255, 255, 255, 255]),
        ];
        this.shadowProgram = program(gl, shadowSource);
        this.shadowUniforms = Object.fromEntries(["viewProjection", ...materialUniforms].map(
            (name) => [name, gl.getUniformLocation(this.shadowProgram, name)]
        ));
        gl.useProgram(this.shadowProgram);
        gl.uniform1i(this.shadowUniforms.baseMap, 0);
        gl.uniform1i(this.shadowUniforms.normalMap, 1);
        this.initShadowMap();
        this.anisotropy = gl.getExtension("EXT_texture_filter_anisotropic");
        this.meshes = [];
        this.textures = new Map();
        this.hidden = new Set(["weapons"]);
        this.mode = "full";
        this.spin = false;
        this.reset();
        if (!platform) {
            this.bindControls();
            this.resizeObserver = new ResizeObserver(() => this.requestFrame());
            this.resizeObserver.observe(canvas);
        }
    }

    initShadowMap() {
        const gl = this.gl;
        this.shadowTexture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, this.shadowTexture);
        gl.texStorage2D(gl.TEXTURE_2D, 1, gl.DEPTH_COMPONENT24, SHADOW_SIZE, SHADOW_SIZE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_COMPARE_MODE, gl.COMPARE_REF_TO_TEXTURE);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_COMPARE_FUNC, gl.LEQUAL);
        this.shadowBuffer = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, this.shadowBuffer);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.TEXTURE_2D, this.shadowTexture, 0);
        gl.drawBuffers([gl.NONE]);
        gl.readBuffer(gl.NONE);
        if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) {
            throw new Error("无法建立阴影缓冲区");
        }
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }

    solid(rgba) {
        const gl = this.gl;
        const texture = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, texture);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array(rgba));
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
        return texture;
    }

    bindControls() {
        const canvas = this.canvas;
        let pointer = null;
        canvas.addEventListener("pointerdown", (event) => {
            if (event.button !== 0) return;
            pointer = {id: event.pointerId, x: event.clientX, y: event.clientY};
            canvas.setPointerCapture(pointer.id);
        });
        canvas.addEventListener("pointermove", (event) => {
            if (event.pointerId !== pointer?.id) return;
            this.turn((pointer.x - event.clientX) * 0.009, (event.clientY - pointer.y) * 0.009);
            pointer.x = event.clientX;
            pointer.y = event.clientY;
        });
        canvas.addEventListener("lostpointercapture", () => { pointer = null; });
        canvas.addEventListener("wheel", (event) => {
            event.preventDefault();
            this.changeZoom(Math.exp(event.deltaY * 0.001));
        }, {passive: false});
        canvas.addEventListener("keydown", (event) => {
            const action = {
                ArrowLeft: () => this.turn(-0.2, 0), ArrowRight: () => this.turn(0.2, 0),
                ArrowUp: () => this.turn(0, 0.15), ArrowDown: () => this.turn(0, -0.15),
                "+": () => this.changeZoom(0.87), "-": () => this.changeZoom(1.15),
            }[event.key];
            if (action) {
                event.preventDefault();
                action();
            }
        });
    }

    turn(yaw, elevation) {
        this.yaw += yaw;
        this.elevation = Math.min(1.35, Math.max(-1.35, this.elevation + elevation));
        this.requestFrame();
    }

    changeZoom(factor) {
        this.zoom = Math.max(0.26, Math.min(3.2, this.zoom * factor));
        this.requestFrame();
    }

    reset() {
        this.yaw = this.mode === "gear" ? 0.27 : 0;
        this.elevation = this.mode === "gear" ? 0.022 : 0;
        this.zoom = 1;
        this.requestFrame();
    }

    /** @param {string} mode @param {string | null} slot */
    setMode(mode, slot = null) {
        this.mode = mode;
        this.slot = slot;
        if (!this.platform) this.canvas.parentElement.dataset.view = mode;
        this.shadowMatrix = null;
        this.reset();
    }

    setHidden(slot, hide) {
        if (hide) this.hidden.add(slot);
        else this.hidden.delete(slot);
        this.shadowMatrix = null;
        this.requestFrame();
    }

    requestFrame() {
        if (this.pendingFrame) return;
        const draw = (time) => {
            this.pendingFrame = 0;
            if (this.spin && this.time) this.yaw += Math.min(50, time - this.time) * 0.00050;
            this.time = time;
            this.render();
            if (this.spin) this.requestFrame();
        };
        this.pendingFrame = this.platform ? this.platform.schedule(draw) : requestAnimationFrame(draw);
    }

    async load(scene, signal, textureURL) {
        if (signal.aborted) return 0;
        if (this.resourceSource !== scene.resourceSource) {
            for (const texture of this.textures.values()) this.gl.deleteTexture(texture);
            this.textures.clear();
            this.resourceSource = scene.resourceSource;
        }
        const query = scene.resourceSource ? "?source=" + encodeURIComponent(scene.resourceSource) : "";
        const source = new Map();
        for (const material of Object.values(scene.materials)) {
            for (const [role, id] of Object.entries(material.maps)) {
                if (!this.textures.has(id)) source.set(id, role);
            }
        }
        const images = await Promise.all([...source].map(async ([id, role]) => {
            try {
                if (this.platform) return [id, role, await this.platform.texture(id, signal)];
                const response = await fetch(textureURL(id) + query, {signal});
                if (!response.ok) return [id, role, null];
                return [id, role, await createImageBitmap(await response.blob(), {
                    colorSpaceConversion: "none", premultiplyAlpha: "none",
                })];
            } catch {
                return [id, role, null];
            }
        }));
        if (signal.aborted) {
            for (const [,, image] of images) image?.close?.();
            return 0;
        }
        const gl = this.gl;
        let missing = 0;
        for (const [id, role, image] of images) {
            if (!image) {
                missing++;
                continue;
            }
            const texture = gl.createTexture();
            gl.bindTexture(gl.TEXTURE_2D, texture);
            if (this.platform) this.platform.uploadTexture(gl, image);
            else gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, image);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, role === "index" ? gl.NEAREST : gl.LINEAR_MIPMAP_LINEAR);
            gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, role === "index" ? gl.NEAREST : gl.LINEAR);
            if (role !== "index") {
                gl.generateMipmap(gl.TEXTURE_2D);
                if (this.anisotropy) {
                    const maxAnisotropy = gl.getParameter(this.anisotropy.MAX_TEXTURE_MAX_ANISOTROPY_EXT);
                    gl.texParameterf(gl.TEXTURE_2D, this.anisotropy.TEXTURE_MAX_ANISOTROPY_EXT, Math.min(8, maxAnisotropy));
                }
            }
            this.textures.set(id, texture);
            image.close?.();
        }
        const used = new Set(Object.values(scene.materials).flatMap((m) => Object.values(m.maps)));
        for (const [id, texture] of this.textures) {
            if (used.has(id)) continue;
            gl.deleteTexture(texture);
            this.textures.delete(id);
        }
        this.clear();
        this.scene = scene;
        for (const material of Object.values(scene.materials)) {
            material.rows = [new Float32Array(128), new Float32Array(128), new Float32Array(128)];
            material.palette.forEach((row, i) => {
                for (let j = 0; j < material.rows.length; j++) material.rows[j].set(row.slice(j*4, j*4+4), i*4);
            });
        }
        this.meshes = scene.meshes.map((part) => this.upload(part));
        this.fullBounds = boundsOf(this.meshes.filter((mesh) => !WEAPONS.has(mesh.slot)));
        const face = this.meshes.filter((mesh) => mesh.slot === "face");
        this.faceBounds = face.length ? boundsOf(face) : null;
        this.slotBounds = new Map();
        this.requestFrame();
        return missing;
    }

    clear() {
        const gl = this.gl;
        for (const mesh of this.meshes) {
            gl.deleteVertexArray(mesh.vao);
            gl.deleteBuffer(mesh.vertexBuffer);
            gl.deleteBuffer(mesh.indexBuffer);
            if (mesh.hair) gl.deleteBuffer(mesh.hair.buffer);
        }
        this.meshes = [];
        this.shadowMatrix = null;
        this.requestFrame();
    }

    dispose() {
        this.spin = false;
        this.resizeObserver?.disconnect();
        this.clear();
        cancelAnimationFrame(this.pendingFrame);
        this.gl.getExtension("WEBGL_lose_context")?.loseContext();
    }

    upload(part) {
        const gl = this.gl;
        const vertices = this.platform ? new Float32Array(this.platform.decode(part.vertices)) : unpack(part.vertices, Float32Array);
        const indices = this.platform ? new Uint16Array(this.platform.decode(part.indices)) : unpack(part.indices, Uint16Array);
        const vao = gl.createVertexArray();
        const vertexBuffer = gl.createBuffer();
        const indexBuffer = gl.createBuffer();
        gl.bindVertexArray(vao);
        gl.bindBuffer(gl.ARRAY_BUFFER, vertexBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, vertices, gl.STATIC_DRAW);
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, indexBuffer);
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);
        [3, 3, 2, 4].forEach((count, i) => {
            gl.enableVertexAttribArray(i);
            gl.vertexAttribPointer(i, count, gl.FLOAT, false, VERTEX_SIZE * 4, [0, 12, 24, 32][i]);
        });
        gl.bindVertexArray(null);
        const material = this.scene.materials[part.material];
        const hair = material.shader === "hair.shpk" && !facialHair(material)
            ? {...hairTriangles(vertices, indices), buffer: gl.createBuffer()}
            : null;
        const aimEyes = material.shader === "iris.shpk" && Boolean(this.scene.eyeCenters);
        return {...part, vertices, indices, vao, vertexBuffer, indexBuffer, hair, aimEyes};
    }

    render() {
        const gl = this.gl;
        const canvas = this.canvas;
        const viewport = this.platform ? this.platform.viewport() : {
            width: canvas.clientWidth, height: canvas.clientHeight, ratio: window.devicePixelRatio || 1,
        };
        const ratio = Math.min(2, viewport.ratio);
        const width = Math.max(1, Math.round(viewport.width * ratio));
        const height = Math.max(1, Math.round(viewport.height * ratio));
        if (canvas.width !== width) canvas.width = width;
        if (canvas.height !== height) canvas.height = height;
        gl.viewport(0, 0, width, height);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        if (!this.meshes.length) return;

        const isolated = this.mode === "gear";
        const meshes = this.meshes.filter((mesh) => isolated ? mesh.slot === this.slot && !mesh.attached
            : (!WEAPONS.has(mesh.slot) || mesh.attached && !this.hidden.has("weapons"))
                && !this.hidden.has(mesh.slot)
                && (!mesh.underHead || this.hidden.has("head")));
        let bounds = this.fullBounds;
        if (isolated) {
            if (!this.slotBounds.has(this.slot)) this.slotBounds.set(this.slot, boundsOf(meshes));
            bounds = this.slotBounds.get(this.slot);
        }
        const {min, max} = bounds;
        const center = min.map((v, i) => (v + max[i]) * 0.5);
        const size = max.map((v, i) => v - min[i]);
        const aspect = width / height;
        const eyes = this.scene.eyeCenters;
        const eyeMidpoint = eyes?.[0].map((value, i) => (value + eyes[1][i]) * 0.5);
        let range = Math.max(size[1] * 1.14, Math.hypot(size[0], size[2]) * 1.08 / aspect, 0.025);
        const portrait = this.mode === "portrait" && this.faceBounds !== null;
        if (portrait) {
            center[0] = eyeMidpoint?.[0] ?? (this.faceBounds.min[0] + this.faceBounds.max[0]) * 0.5;
            const faceBottom = this.faceBounds.min[1];
            const headHeight = max[1] - faceBottom;
            center[1] = (max[1] + faceBottom) * 0.5 - headHeight * 0.042;
            center[2] = this.faceBounds.max[2] * 0.74;
            const faceWidth = this.faceBounds.max[0] - this.faceBounds.min[0];
            range = Math.max(headHeight * 1.02, faceWidth * 1.08 / aspect);
        }
        const distance = portrait ? Math.max(0.42, range * 1.85) : Math.max(0.25, 3 * Math.max(...size));
        range *= this.zoom;
        const radius = distance * Math.cos(this.elevation);
        const eye = [
            center[0] + Math.sin(this.yaw) * radius,
            center[1] + Math.sin(this.elevation) * distance,
            center[2] + Math.cos(this.yaw) * radius,
        ];
        if (!this.shadowMatrix) this.renderShadow(meshes, bounds);
        gl.viewport(0, 0, width, height);
        gl.useProgram(this.program);
        gl.activeTexture(gl.TEXTURE0 + MAPS.length);
        gl.bindTexture(gl.TEXTURE_2D, this.shadowTexture);
        const u = this.uniforms;
        gl.uniformMatrix4fv(u.lightProjection, false, this.shadowMatrix);
        gl.uniform4fv(u.eyePlane, this.scene.eyePlane || [1, 0, 0, 0]);
        gl.uniform1i(u.neutralLight, Boolean(this.neutralLight));
        gl.uniform1i(u.portraitView, this.mode === "portrait");
        const far = portrait ? distance + size[1] : distance * 2;
        gl.uniformMatrix4fv(u.viewProjection, false, cameraMatrix(eye, center, range * aspect, range, far, portrait));
        gl.uniform3fv(u.camera, eye);

        if (eyes) {
            // Aim from the facial rig's neutral axis so the sculpted iris offset is preserved.
            // The eye line stays at the default camera position during orbiting.
            const direction = portrait ? [0, center[1] - eyeMidpoint[1], center[2] + distance - eyeMidpoint[2]]
                : [0, 0, 1];
            gl.uniform3fv(u["eyeCenters[0]"], eyes.flat());
            gl.uniform4fv(u.eyeRotation, gazeRotation(this.scene.faceForward || [0, 0, 1], direction));
        }
        gl.uniform1i(u.hairEdgePass, false);
        this.draw(meshes, "opaque");
        const detail = meshes.filter((mesh) => facialHair(this.scene.materials[mesh.material]));
        const hair = meshes.filter((mesh) => mesh.hair);
        if (detail.length || hair.length) {
            gl.enable(gl.BLEND);
            gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);
            gl.depthMask(false);
            this.draw(detail, "detail");
            if (hair.length) {
                const direction = eye.map((v, i) => (v - center[i]) / distance);
                const orderKey = direction.map((v) => Math.round(v * 64)).join(",");
                hair.sort((a, b) => projectedDepth(a.hair.center, 0, direction)
                    - projectedDepth(b.hair.center, 0, direction));
                gl.uniform1i(u.hairEdgePass, true);
                this.draw(hair, "hair", direction, orderKey);
            }
            gl.depthMask(true);
            gl.disable(gl.BLEND);
        }
    }

    renderShadow(meshes, {min, max}) {
        const gl = this.gl;
        const center = min.map((n, i) => (n + max[i]) * 0.5);
        const range = Math.max(0.1, Math.hypot(...max.map((n, i) => n - min[i])) * 1.12);
        const eye = LIGHT_DIRECTION.map((v, i) => center[i] + range * 1.5 * v);
        this.shadowMatrix = cameraMatrix(eye, center, range, range, range * 3);
        gl.bindFramebuffer(gl.FRAMEBUFFER, this.shadowBuffer);
        gl.viewport(0, 0, SHADOW_SIZE, SHADOW_SIZE);
        gl.clear(gl.DEPTH_BUFFER_BIT);
        gl.useProgram(this.shadowProgram);
        gl.uniformMatrix4fv(this.shadowUniforms.viewProjection, false, this.shadowMatrix);
        this.draw(meshes, "shadow");
        gl.bindFramebuffer(gl.FRAMEBUFFER, null);
    }

    draw(meshes, pass, direction, orderKey) {
        const gl = this.gl;
        const shadow = pass === "shadow";
        let last = null;
        for (const mesh of meshes) {
            const material = this.scene.materials[mesh.material];
            if (shadow && material.isFace && material.shader !== "skin.shpk") continue;
            if (pass === "opaque" && facialHair(material)) continue;
            if (mesh.material !== last) {
                this.applyMaterial(material, shadow);
                last = mesh.material;
            }
            gl.bindVertexArray(mesh.vao);
            if (!shadow) gl.uniform1i(this.uniforms.aimEyes, mesh.aimEyes);
            if (pass === "hair") this.bindHairIndices(mesh, direction, orderKey);
            gl.drawElements(gl.TRIANGLES, mesh.indices.length, gl.UNSIGNED_SHORT, 0);
            if (pass === "hair") gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, mesh.indexBuffer);
        }
        gl.bindVertexArray(null);
    }

    bindHairIndices({hair, indices}, direction, orderKey) {
        const gl = this.gl;
        gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, hair.buffer);
        if (hair.orderKey === orderKey) return;
        const {centers, order, depth, sorted} = hair;
        for (let i = 0; i < order.length; i++) depth[i] = projectedDepth(centers, i * 3, direction);
        order.sort((a, b) => depth[a] - depth[b]);
        for (let i = 0; i < order.length; i++) {
            const source = order[i] * 3;
            sorted[i * 3] = indices[source];
            sorted[i * 3 + 1] = indices[source + 1];
            sorted[i * 3 + 2] = indices[source + 2];
        }
        gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, sorted, gl.DYNAMIC_DRAW);
        hair.orderKey = orderKey;
    }

    applyMaterial(material, shadow) {
        const gl = this.gl;
        const u = shadow ? this.shadowUniforms : this.uniforms;
        const surface = facialHair(material) ? 7 : SURFACES[material.shader] || 0;
        gl.uniform1i(u.surface, surface);
        gl.uniform1f(u.alphaCutoff, material.alphaCutoff ?? 0.40);
        const count = shadow ? 2 : MAPS.length;
        for (let i = 0; i < count; i++) {
            const role = MAPS[i];
            const texture = this.textures.get(material.maps[role]);
            gl.activeTexture(gl.TEXTURE0 + i);
            gl.bindTexture(gl.TEXTURE_2D, texture || this.fallback[i]);
            gl.uniform1i(u[`has${role[0].toUpperCase()}${role.slice(1)}Map`], Boolean(texture));
        }
        if (shadow) return;
        const tones = this.scene.colors;
        const fallback = [0.4, 0.29, 0.24];
        const tint = [null, "skin", "hair", "eyeRight", "hair", null, null, "hair"][surface];
        const accent = [null, "skin", "highlight", "eyeLeft", "hair"][surface];
        gl.uniform3fv(u.tint, color(tones[tint], fallback));
        gl.uniform3fv(u.accent, color(tones[accent], fallback));
        gl.uniform3fv(u.hairSheen, color(tones.hairSheen, [0.2, 0.2, 0.2]));
        const lips = color(tones.lips?.slice(0, 7), [0, 0, 0]);
        gl.uniform4fv(u.lips, [...lips, tones.lips ? Number.parseInt(tones.lips.slice(-2), 16) / 255 : 0]);
        gl.uniform1i(u.isFace, Boolean(material.isFace));
        gl.uniform3fv(u.diffuseTint, material.diffuseTint || [1, 1, 1]);
        gl.uniform3fv(u.specularTint, material.specularTint || [1, 1, 1]);
        gl.uniform3fv(u.scleraTint, material.scleraTint || [1, 1, 1]);
        gl.uniform3fv(u.eyeReflection, material.eyeReflection?.length === 3 ? material.eyeReflection : [0.35, 0.35, 0.35]);
        gl.uniform3fv(u.eyeEmission, material.eyeEmission || [0, 0, 0]);
        gl.uniform3fv(u.dye1, color(material.dyes[0], [-1, -1, -1]));
        gl.uniform3fv(u.dye2, color(material.dyes[1], [-1, -1, -1]));
        gl.uniform1i(u.colorRows, material.palette.length);
        if (material.palette.length) {
            gl.uniform4fv(u["diffuseRows[0]"], material.rows[0]);
            gl.uniform4fv(u["specularRows[0]"], material.rows[1]);
            gl.uniform4fv(u["emissionRows[0]"], material.rows[2]);
        }
    }
}
