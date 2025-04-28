# bevity
use unity as your bevy editor

## instructions

0. in a new unity project, install the UnityGLTF package

1. drag all the .cs files from this repo into a unity project

2. create a game object in your unity scene and put a "bevity registry" component on it

3. start your bevy app with bevy remote protocol enabled

4. set your host and port in the bevity registry component in the unity object inspector. 

5. click "fetch json data" on the registry component inspector

6. create another game object and add "bevity components" to it

7. click "reload schemas" in the inspector of the bevity components

8. now you can search for components and click them to add them

9. right click a selection of game objects and choose UnityGLTF -> Export as GLB/GLTF. any objects with bevity components will get their component data exported to the gltf.

10. now with `serde_json` and a simple observer, you can reflect the component data out from the `GLTFExtras`

```rs
fn apply_bevity_components(
    trigger: Trigger<
        OnAdd,
        (
            GltfExtras,
        ),
    >,
    type_registry: Res<AppTypeRegistry>,
    gltf_extras: Query<&GltfExtras>,
    names: Query<&Name>,
    mut commands: Commands,
) {
    let entity = trigger.target();
    let gltf_extra = gltf_extras.get(entity).map(|v| &v.value);
    for extras in [
        gltf_extra,
    ]
    .iter()
    .filter_map(|p| p.ok())
    {
        let obj = match serde_json::from_str(extras) {
            Ok(Value::Object(obj)) => obj,
            Ok(Value::Null) => {
                if let Ok(name) = names.get(entity) {
                    trace!(
                        "entity {:?} with name {name} had gltf extras which could not be parsed as a serde_json::Value::Object; parsed as Null",
                        entity
                    );
                } else {
                    trace!(
                        "entity {:?} with no Name had gltf extras which could not be parsed as a serde_json::Value::Object; parsed as Null",
                        entity
                    );
                }
                continue;
            }
            Ok(value) => {
                let name = names.get(entity).ok();
                trace!(?entity, ?name, parsed_as=?value, "gltf extras which could not be parsed as a serde_json::Value::Object");
                continue;
            }
            Err(err) => {
                let name = names.get(entity).ok();
                trace!(
                    ?entity,
                    ?name,
                    ?err,
                    "gltf extras which could not be parsed as a serde_json::Value::Object"
                );
                continue;
            }
        };

        let bevity = match obj.get("bevity") {
            Some(Value::Array(components)) => components,
            _ => continue
        };

        for json_component in bevity.iter() {
            let type_registry = type_registry.read();

            let reflect_deserializer =
                ReflectDeserializer::new(&type_registry);
            let reflect_value = match reflect_deserializer
                .deserialize(json_component)
            {
                Ok(value) => value,
                Err(err) => {
                    error!(
                        ?err,
                        ?obj,
                        "failed to instantiate component data from glTF data"
                    );
                    continue;
                }
            };

            commands
                .entity(entity)
                .insert_reflect(reflect_value);
        }
    }
}
```


prefabs will complain about a missing bevy registry but they should otherwise work when added to a scene where the type hasn't been changed out from beneath them. you'll lose your data if you change the underlying bevy type for a bevy component on a prefab.
