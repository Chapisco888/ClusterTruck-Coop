# Blender script: bakes the Quaternius "Animated Human" (CC0) into human.bin, the per-frame vertex format the mod reads.
# The game (Unity 5.3) can't load skinned FBX at runtime, so every animation is stored as vertex positions per frame.
#   blender -b "Animated Human.blend" -P export_human.py -- human.bin
import bpy, struct, sys

out = sys.argv[sys.argv.index('--') + 1]
BONES = ['Head', 'Spine2']
CLIPS = [('Idle', 3), ('Walk', 2), ('Run', 1), ('Jump', 2), ('Death', 2)]  # action name, frame step

scene = bpy.context.scene
arm = next(o for o in scene.objects if o.type == 'ARMATURE')
obj = next(o for o in scene.objects if o.type == 'MESH' and any(m.type == 'ARMATURE' for m in o.modifiers))
print('EXPORT mesh', obj.name, 'armature', arm.name, 'fps', scene.render.fps, 'actions', [a.name for a in bpy.data.actions])


def frame_verts(f):
    scene.frame_set(f)
    ev = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    me = ev.to_mesh()
    mw = ev.matrix_world
    co = [mw @ v.co for v in me.vertices]
    ev.to_mesh_clear()
    conv = lambda c: (-c.x, c.z, -c.y)  # Blender Z-up right-handed -> Unity Y-up left-handed
    bones = []
    for name in BONES:  # where accessories hang: position + the bone's Y (along the bone) and Z axes
        m = arm.matrix_world @ arm.pose.bones[name].matrix
        bones += [conv(m.translation), conv(m.col[1].xyz), conv(m.col[2].xyz)]
    return [conv(c) for c in co], bones


# topology + uvs from the rest of the evaluated mesh (constant across frames)
ev = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
me = ev.to_mesh()
me.calc_loop_triangles()
uv = me.uv_layers.active.data
loops = []
for t in me.loop_triangles:
    for li in t.loops:
        loops.append((me.loops[li].vertex_index, uv[li].uv.x, uv[li].uv.y))
nverts = len(me.vertices)
ev.to_mesh_clear()

with open(out, 'wb') as fh:
    fh.write(b'CTH2')
    fh.write(struct.pack('<ii', nverts, len(loops)))
    for l in loops:
        fh.write(struct.pack('<iff', *l))
    fh.write(struct.pack('<i', len(CLIPS)))
    for name, step in CLIPS:
        act = bpy.data.actions[name]
        arm.animation_data.action = act
        a, b = int(act.frame_range[0]), int(act.frame_range[1])
        frames = [frame_verts(f) for f in range(a, b + 1, step)]
        fh.write(struct.pack('<B', len(name)) + name.encode('ascii'))
        fh.write(struct.pack('<fi', scene.render.fps / step, len(frames)))
        for fr, bones in frames:
            assert len(fr) == nverts and len(bones) == 3 * len(BONES)
            for v in fr + bones:
                fh.write(struct.pack('<fff', *v))
        print('EXPORT bones', name, [[round(x, 2) for x in b] for b in frames[0][1]])
        frames = [fr for fr, bones in frames]
        ys = [v[1] for v in frames[0]]
        cx = lambda fr: (sum(v[0] for v in fr) / nverts, sum(v[2] for v in fr) / nverts)
        print('EXPORT clip', name, 'frames', len(frames), 'range', a, b, 'y', round(min(ys), 3), round(max(ys), 3),
              'center first/last', [round(x, 2) for x in cx(frames[0])], [round(x, 2) for x in cx(frames[-1])])
print('EXPORT done verts', nverts, 'loops', len(loops))
