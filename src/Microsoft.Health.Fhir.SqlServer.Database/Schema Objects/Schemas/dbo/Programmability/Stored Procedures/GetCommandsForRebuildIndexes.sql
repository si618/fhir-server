--IF object_id('GetCommandsForRebuildIndexes') IS NOT NULL DROP PROCEDURE dbo.GetCommandsForRebuildIndexes
GO
CREATE PROCEDURE dbo.GetCommandsForRebuildIndexes
WITH EXECUTE AS 'dbo'
AS
set nocount on
DECLARE @SP varchar(100) = 'GetCommandsForRebuildIndexes'
       ,@Mode varchar(100) = 'PS=PartitionScheme_ResourceTypeId'
       ,@st datetime = getUTCdate()
       ,@Tbl varchar(100)
       ,@TblInt varchar(100)
       ,@Ind varchar(200)
       ,@Supported bit
       ,@Txt varchar(max)
       ,@Rows bigint
       ,@ResourceTypeId smallint

BEGIN TRY
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Start'

  DECLARE @Commands TABLE (Tbl varchar(100), Txt varchar(max), Rows bigint)
  DECLARE @ResourceTypes TABLE (ResourceTypeId smallint PRIMARY KEY)
  DECLARE @Indexes TABLE (Ind varchar(200) PRIMARY KEY)
  DECLARE @Tables TABLE (name varchar(100) PRIMARY KEY, Supported bit)

  INSERT INTO @Tables EXECUTE dbo.GetPartitionedTables @IncludeNotDisabled = 1, @IncludeNotSupported = 1
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Info',@Target='@Tables',@Action='Insert',@Rows=@@rowcount

  WHILE EXISTS (SELECT * FROM @Tables)
  BEGIN
    SELECT TOP 1 @Tbl = name, @Supported = Supported FROM @Tables ORDER BY name

    IF @Supported = 0
    BEGIN
      INSERT INTO @Commands
        SELECT @Tbl, 'ALTER INDEX '+name+' ON dbo.'+@Tbl+' REBUILD', convert(bigint,9e18) FROM sys.indexes WHERE object_id = object_id(@Tbl) AND is_disabled = 1
      EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Info',@Target='@Commands',@Action='Insert',@Rows=@@rowcount,@Text='Not supported tables with disabled indexes'
    END
    ELSE
    BEGIN
      DELETE FROM @ResourceTypes
      INSERT INTO @ResourceTypes 
        SELECT ResourceTypeId = convert(smallint,substring(name,charindex('_',name)+1,6))
          FROM sys.sysobjects 
          WHERE name LIKE @Tbl+'[_]%'
      EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Info',@Target='@ResourceTypes',@Action='Insert',@Rows=@@rowcount

      WHILE EXISTS (SELECT * FROM @ResourceTypes)
      BEGIN
        SET @ResourceTypeId = (SELECT TOP 1 ResourceTypeId FROM @ResourceTypes ORDER BY ResourceTypeId)
        SET @TblInt = @Tbl+'_'+convert(varchar,@ResourceTypeId)
        SET @Rows = (SELECT rows FROM sysindexes WHERE id = object_id(@TblInt) AND indid IN (0,1)) 

        -- add check constraints
        IF NOT EXISTS (SELECT * FROM sys.sysconstraints WHERE id = object_id(@TblInt) AND colid = (SELECT column_id FROM sys.columns WHERE object_id = object_id(@TblInt) AND name = 'ResourceTypeId') AND status & 4 = 4)
        BEGIN
          INSERT INTO @Commands SELECT @TblInt, 'ALTER TABLE dbo.'+@TblInt+' ADD CHECK (ResourceTypeId = '+convert(varchar,@ResourceTypeId)+')', @Rows
          EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Info',@Target=@TblInt,@Action='Add command',@Rows=@@rowcount,@Text='Add check constraint'
        END

        -- add indexes
        DELETE FROM @Indexes
        INSERT INTO @Indexes SELECT name FROM sys.indexes WHERE object_id = object_id(@Tbl) AND index_id > 1 -- indexes in target table
        WHILE EXISTS (SELECT * FROM @Indexes)
        BEGIN
          SET @Ind = (SELECT TOP 1 Ind FROM @Indexes ORDER BY Ind)

          IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = object_id(@TblInt) AND name = @Ind) -- not existing indexes in source table
          BEGIN
            EXECUTE dbo.GetIndexCommands @Tbl = @Tbl, @Ind = @Ind, @AddPartClause = 0, @IncludeClustered = 0, @Txt = @Txt OUT
            SET @Txt = replace(@Txt,'['+@Tbl+']',@TblInt)
            IF @Txt IS NOT NULL
            BEGIN
              INSERT INTO @Commands SELECT @TblInt, @Txt, @Rows
              EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Info',@Target=@TblInt,@Action='Add command',@Rows=@@rowcount,@Text=@Txt
            END
          END

          DELETE FROM @Indexes WHERE Ind = @Ind
        END

        DELETE FROM @ResourceTypes WHERE ResourceTypeId = @ResourceTypeId
      END
    END

    DELETE FROM @Tables WHERE name = @Tbl
  END

  SELECT Tbl, Txt FROM @Commands ORDER BY Rows DESC, Tbl, CASE WHEN Txt LIKE 'CREATE %' THEN 0 ELSE 1 END -- add index creates before checks
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Info',@Target='@Commands',@Action='Select',@Rows=@@rowcount

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error',@Start=@st
END CATCH
GO
