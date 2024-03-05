/* Fragment from PgInstanceRepository.SetStatuses():
else if (instance.Status.ReadStatus == ReadStatus.Read && instance.Data.Count > 0 && !instance.Data.Exists(d => d.IsRead))
    instance.Status.ReadStatus = ReadStatus.Unread; //yt 14382 updates for 1min25s
*/
DO
$do$
DECLARE
    _startId int := 0;
    _maxId int := (select max(id) from storage.instances);
    _updateCount int := 0;
    _updateCountTotal int := 0;
    _batchSize int := 1000;
BEGIN
    WHILE _startId <= _maxId
    LOOP
        update storage.instances u set instance =  jsonb_set(i.instance, '{Status, ReadStatus}', '0')
            from storage.instances i
                where i.instance -> 'Status' ->> 'ReadStatus' = '1' and i.id = u.id
                    and not exists
                        (select * from  storage.dataelements d where d.element -> 'IsRead' = 'true' and d.instanceinternalid = i.id) 
                    and i.id between _startId and _startId + _batchSize;

        GET DIAGNOSTICS _updateCount = ROW_COUNT;
        RAISE NOTICE 'StartId: %, updates: %', _startId, _updateCount;
        _startId := _startId + _batchSize;
        _updateCountTotal := _updateCountTotal + _updateCount;
    END LOOP;
    RAISE NOTICE 'Total updates: %', _updateCountTotal;
END
$do$

/* Fragment from PgInstanceRepository.SetStatuses():
if (instance.Status.ReadStatus == ReadStatus.Read && instance.Data.Exists(d => !d.IsRead))
    instance.Status.ReadStatus = ReadStatus.UpdatedSinceLastReview; //yt 30963 updates for 1min15s
*/
DO
$do$
DECLARE
    _startId int := 0;
    _maxId int := (select max(id) from storage.instances);
    _updateCount int := 0;
    _updateCountTotal int := 0;
    _batchSize int := 1000;
BEGIN
    WHILE _startId <= _maxId
    LOOP
        update storage.instances u set instance =  jsonb_set(i.instance, '{Status, ReadStatus}', '2')
            from storage.instances i join storage.dataelements d on d.instanceinternalid = i.id
            where d.element -> 'IsRead' = 'false' and i.instance -> 'Status' ->> 'ReadStatus' = '1' and i.id = u.id
                and i.id between _startId and _startId + _batchSize;

        GET DIAGNOSTICS _updateCount = ROW_COUNT;
        RAISE NOTICE 'StartId: %, updates: %', _startId, _updateCount;
        _startId := _startId + _batchSize;
        _updateCountTotal := _updateCountTotal + _updateCount;
    END LOOP;
    RAISE NOTICE 'Total updates: %', _updateCountTotal;
END
$do$

/*
    InstanceHelper.FindLastChanged
    Update instance from newest updated element (if updated after instance)
    //yt 86368 updates for 5min55s
*/
DO
$do$
DECLARE
    _startId int := 0;
    _maxId int := (select max(id) from storage.instances);
    _updateCount int := 0;
    _updateCountTotal int := 0;
    _batchSize int := 1000;
BEGIN
    WHILE _startId <= _maxId
    LOOP
        with changedToUpdate as (
            select distinct on (i.id) i.id,
                d.id as ElementId,
                
                -- Make sure that lastChanged has the Postgres precision (6 digits). The timestamp from C# DateTime and then json serialize has 7 digits
                (d.element ->> 'LastChanged')::TIMESTAMPTZ as ElementLastChangedAsTz,

                REPLACE(((d.element ->> 'LastChanged')::TIMESTAMPTZ AT TIME ZONE 'UTC')::TEXT, ' ', 'T') || 'Z' as ElementLastChangedAsText,
                d.element ->> 'LastChangedBy' as ElementLastChangedByAsText
            from storage.instances i
                join storage.dataelements d on d.instanceinternalid = i.id
            where d.element ->> 'LastChanged' > i.instance ->> 'LastChanged' and i.id between _startId and _startId + _batchSize
            order by i.id, d.element ->> 'LastChanged' desc
        ),
        u2 as (
        update storage.instances
            set lastchanged = changedToUpdate.ElementLastChangedAsTz,
            instance = instance
                || jsonb_set('{"LastChanged":""}', '{LastChanged}', to_jsonb(ElementLastChangedAsText))
                || jsonb_set('{"LastChangedBy":""}', '{LastChangedBy}', to_jsonb(ElementLastChangedByAsText))
            from changedToUpdate
                where changedToUpdate.id = storage.instances.id
        )
        update storage.dataelements
            set element = element
                || jsonb_set('{"LastChanged":""}', '{LastChanged}', to_jsonb(ElementLastChangedAsText))
            from changedToUpdate
                where changedToUpdate.ElementId = storage.dataelements.id;        

        GET DIAGNOSTICS _updateCount = ROW_COUNT;
        RAISE NOTICE 'StartId: %, updates: %', _startId, _updateCount;
        _startId := _startId + _batchSize;
        _updateCountTotal := _updateCountTotal + _updateCount;
    END LOOP;
    RAISE NOTICE 'Total updates: %', _updateCountTotal;
END
$do$