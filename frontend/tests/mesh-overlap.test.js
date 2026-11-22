import assert from "node:assert/strict";
import test from "node:test";
import {overlappingAccessorySlots, VERTEX_SIZE} from "../src/renderer/mesh-overlap.js";
import {Viewer} from "../src/renderer/viewer.js";

function mesh(slot, points, properties = {}) {
    const vertices = new Float32Array(points.length * VERTEX_SIZE);
    points.forEach((point, i) => vertices.set(point, i * VERTEX_SIZE));
    return {slot, vertices, indices: Uint16Array.from(points.keys()), material: "equipment", ...properties};
}

const surface = [[0, 0, 0], [2, 0, 0], [0, 2, 0]];
const crossing = [[0.5, 0.5, -1], [0.5, 1, 1], [1, 0.5, 1]];
const clear = [[3, 3, 0], [4, 3, 0], [3, 4, 0]];

test("hides a whole accessory slot if any of its meshes intersects equipped triangles", () => {
    const hood = mesh("head", Array.from({length: 10}, (_, i) =>
        surface.map(([x, y, z]) => [x + i * 5, y, z])).flat());
    const accessories = [mesh("ears", clear), mesh("ears", crossing), mesh("neck", clear)];
    assert.deepEqual(overlappingAccessorySlots(accessories, [hood]), new Set(["ears"]));
});

test("distinguishes coplanar intersections from overlapping bounding boxes", () => {
    const bracelet = mesh("wrists", [[0.4, 0.4, 0], [1, 0.4, 0], [0.4, 1, 0]]);
    const distantRing = mesh("left_ring", [[1.25, 1.25, 0], [1.8, 1.25, 0], [1.25, 1.8, 0]]);
    const offsetRing = mesh("right_ring", crossing.map(([x, y, z]) => [x, y, z + 3]));
    assert.deepEqual(overlappingAccessorySlots([bracelet, distantRing, offsetRing], [mesh("hands", surface)]),
        new Set(["wrists"]));
});

test("the character view hides weapons and rechecks accessory overlap when headgear is toggled", () => {
    const viewer = Object.assign(Object.create(Viewer.prototype), {
        hidden: new Set(),
        requestFrame() {},
        scene: {
            gear: [{slot: "head", model: 1}, {slot: "hands", model: 1}, {slot: "body", model: 0}],
            materials: {equipment: {shader: "character.shpk"}, skin: {shader: "skin.shpk"}},
        },
        meshes: [
            mesh("main_hand", surface, {attached: true}), mesh("off_hand", surface, {attached: true}),
            mesh("off_hand", clear, {attached: false}),
            mesh("head", surface), mesh("ears", crossing),
            mesh("hands", surface, {material: "skin"}), mesh("left_ring", clear), mesh("right_ring", clear),
            mesh("body", clear), mesh("neck", clear),
        ],
    });
    assert.deepEqual(viewer.visibleCharacterMeshes().map(part => part.slot),
        ["head", "hands", "left_ring", "right_ring", "body", "neck"]);
    viewer.setHidden("head", true);
    assert.deepEqual(viewer.visibleCharacterMeshes().map(part => part.slot),
        ["ears", "hands", "left_ring", "right_ring", "body", "neck"]);
});
