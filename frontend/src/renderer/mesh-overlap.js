export const VERTEX_SIZE = 12;
const CONTACT_TOLERANCE = 1e-5;
const LEAF_TRIANGLES = 8;

export function boundsOf(meshes) {
    const min = [Infinity, Infinity, Infinity];
    const max = [-Infinity, -Infinity, -Infinity];
    for (const {vertices, indices} of meshes) {
        for (const index of indices) {
            for (let axis = 0; axis < 3; axis++) {
                const value = vertices[index * VERTEX_SIZE + axis];
                min[axis] = Math.min(min[axis], value);
                max[axis] = Math.max(max[axis], value);
            }
        }
    }
    if (!meshes.length) return {min: [-0.5, 0, -0.5], max: [0.5, 1, 0.5]};
    return {min, max};
}

function boundsOverlap(a, b) {
    return a.min.every((value, i) => value <= b.max[i] + CONTACT_TOLERANCE
        && b.min[i] <= a.max[i] + CONTACT_TOLERANCE);
}

function subtract(a, b) {
    return [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
}

function cross(a, b) {
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
}

function dot(a, b) {
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
}

function trianglesOf({vertices, indices}) {
    function position(index) {
        const offset = index * VERTEX_SIZE;
        return [vertices[offset], vertices[offset + 1], vertices[offset + 2]];
    }

    const triangles = [];
    for (let i = 0; i + 2 < indices.length; i += 3) {
        const a = position(indices[i]);
        const b = position(indices[i + 1]);
        const c = position(indices[i + 2]);
        const edges = [subtract(b, a), subtract(c, b), subtract(a, c)];
        const normal = cross(edges[0], edges[1]);
        if (!dot(normal, normal)) continue;
        triangles.push({
            points: [a, b, c], edges, normal,
            min: a.map((value, axis) => Math.min(value, b[axis], c[axis])),
            max: a.map((value, axis) => Math.max(value, b[axis], c[axis])),
        });
    }
    return triangles;
}

function triangleTree(triangles) {
    if (!triangles.length) return null;
    const min = [Infinity, Infinity, Infinity];
    const max = [-Infinity, -Infinity, -Infinity];
    for (const triangle of triangles) {
        for (let axis = 0; axis < 3; axis++) {
            min[axis] = Math.min(min[axis], triangle.min[axis]);
            max[axis] = Math.max(max[axis], triangle.max[axis]);
        }
    }
    if (triangles.length <= LEAF_TRIANGLES) return {min, max, triangles};
    const span = subtract(max, min);
    const axis = span.indexOf(Math.max(...span));
    triangles.sort((a, b) => a.min[axis] + a.max[axis] - b.min[axis] - b.max[axis]);
    const middle = triangles.length >> 1;
    return {min, max, left: triangleTree(triangles.slice(0, middle)), right: triangleTree(triangles.slice(middle))};
}

function separates(axis, a, b) {
    const tolerance = CONTACT_TOLERANCE * Math.hypot(...axis);
    const aProjection = a.map(point => dot(point, axis));
    const bProjection = b.map(point => dot(point, axis));
    return Math.max(...aProjection) + tolerance < Math.min(...bProjection)
        || Math.max(...bProjection) + tolerance < Math.min(...aProjection);
}

function trianglesOverlap(a, b) {
    if (!boundsOverlap(a, b) || separates(a.normal, a.points, b.points) || separates(b.normal, a.points, b.points))
        return false;
    for (const edgeA of a.edges) {
        for (const edgeB of b.edges) {
            if (separates(cross(edgeA, edgeB), a.points, b.points)) return false;
        }
    }
    // Coplanar triangles also need separating axes in their shared plane.
    return !a.edges.some(edge => separates(cross(a.normal, edge), a.points, b.points))
        && !b.edges.some(edge => separates(cross(b.normal, edge), a.points, b.points));
}

function intersectsTree(triangle, tree) {
    if (!tree || !boundsOverlap(triangle, tree)) return false;
    if (tree.triangles) return tree.triangles.some(other => trianglesOverlap(triangle, other));
    return intersectsTree(triangle, tree.left) || intersectsTree(triangle, tree.right);
}

export function overlappingAccessorySlots(accessories, equipment) {
    const hidden = new Set();
    if (!accessories.length || !equipment.length) return hidden;
    const armor = equipment.map(mesh => ({mesh, bounds: boundsOf([mesh]), tree: undefined}));
    for (const accessory of accessories) {
        if (hidden.has(accessory.slot)) continue;
        const bounds = boundsOf([accessory]);
        let triangles;
        for (const part of armor) {
            if (!boundsOverlap(bounds, part.bounds)) continue;
            triangles ??= trianglesOf(accessory);
            if (part.tree === undefined) part.tree = triangleTree(trianglesOf(part.mesh));
            if (triangles.some(triangle => intersectsTree(triangle, part.tree))) {
                hidden.add(accessory.slot);
                break;
            }
        }
    }
    return hidden;
}
