package com.breezejs.save;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

import com.breezejs.util.Json;
import com.breezejs.util.Reflect;

public class SaveWorkState {

	private ContextProvider context;
	private List<Map> entityMaps;
	public Map<Class, List<EntityInfo>> saveMap;
	public List<EntityInfo> entitiesWithAutoGeneratedKeys;
	public List<KeyMapping> keyMappings;
	public List<EntityError> entityErrors;

	/**
	 * @param context
	 * @param entityMaps raw name-value pairs of entity properties, from JSON
	 */
	public SaveWorkState(ContextProvider context, List<Map> entityMaps) {
		this.context = context;
		this.entityMaps = entityMaps;
		this.saveMap = new HashMap<Class, List<EntityInfo>>();
		this.entitiesWithAutoGeneratedKeys = new ArrayList<EntityInfo>();
	}

	/** Build the saveMap, and call context.beforeSaveEntity/ies */
	protected void beforeSave() throws EntityErrorsException {
		for (Object o : entityMaps) {
			EntityInfo entityInfo = createEntityInfoFromJson((Map) o);

			// don't put it in the saveMap if it was rejected by beforeSaveEntity
			if (context.beforeSaveEntity(entityInfo)) {
				addToSaveMap(entityInfo);

				if (entityInfo.autoGeneratedKey != null) {
					entitiesWithAutoGeneratedKeys.add(entityInfo);
				}
			}
		}
		saveMap = context.beforeSaveEntities(saveMap);
	}

	/** Call context.afterSaveEntities */
	protected void afterSave() throws EntityErrorsException {
		context.afterSaveEntities(saveMap, keyMappings);
	}

	private void addToSaveMap(EntityInfo entityInfo) {
		Class clazz = entityInfo.entity.getClass();

		List<EntityInfo> entityInfos = saveMap.get(clazz);
		if (entityInfos == null) {
			entityInfos = new ArrayList<EntityInfo>();
			saveMap.put(clazz, entityInfos);
		}
		entityInfos.add(entityInfo);
	}

	/**
	 * @param map raw name-value pairs from JSON
	 * @return populated EntityInfo
	 */
	private EntityInfo createEntityInfoFromJson(Map map) {
		EntityInfo info = new EntityInfo();

		Map aspect = (Map) map.get("entityAspect");
		map.remove("entityAspect");

		String entityTypeName = (String) aspect.get("entityTypeName");
		Class type = Reflect.lookupEntityType(entityTypeName);
		info.entity = Json.fromMap(type, map);

		info.entityState = EntityState.valueOf((String) aspect.get("entityState"));
		info.originalValuesMap = (Map) aspect.get("originalValuesMap");
		info.unmappedValuesMap = (Map) aspect.get("unmappedValuesMap");
		Map autoKey = (Map) aspect.get("autoGeneratedKey");
		if (autoKey != null) {
			info.autoGeneratedKey = new AutoGeneratedKey(
					info.entity, (String) autoKey.get("propertyName"),
					(String) autoKey.get("autoGeneratedKeyType"));
		}
		return info;
	}

	/**
	 * Populate a new SaveResult with the entities and keyMappings. If there are
	 * entityErrors, populate it with those instead.
	 */
	public SaveResult toSaveResult() {
		if (entityErrors != null) {
			return new SaveResult(entityErrors);
		} else {
			List<Object> entities = new ArrayList<Object>();
			for (List<EntityInfo> infos : saveMap.values()) {
				for (EntityInfo info : infos) {
					entities.add(info.entity);
				}
			}
			return new SaveResult(entities, keyMappings);
		}
	}

}
