using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Oracle.ManagedDataAccess.Client;
using Verizon.API.DataModels.Models;
using Verizon.API.Services.Repository;

namespace Verizon.API.Services.Services
{
    public class BomPartService : IBomPartService
    {
        DBHandler dbHandler = new DBHandler();
        private readonly IROHistService _RoHistService;
        private readonly ICommonCodeService _CommonCodeService;
        private readonly ISeqNbrService _SeqNbrService;
        private readonly IInventoryTxnHistoryService _InventoryTxnHistoryService;

        public BomPartService(ICommonCodeService CommonCodeService, IROHistService RoHistService,
                                        IInventoryTxnHistoryService InventoryTxnHistoryService, ISeqNbrService SeqNbrService)
        {
            _CommonCodeService = CommonCodeService;
            _RoHistService = RoHistService;
            _InventoryTxnHistoryService = InventoryTxnHistoryService;
            _SeqNbrService = SeqNbrService;
        }



        public CS_Result GetMakeWholeRefurbList(string assetTagId, string userName, string currHubId)
        {
            var roRec = _RoHistService.GetRoHistRecordFromRoAssetTag(assetTagId);
            if (!roRec.Success) return new CS_Result(false, $@"Item {assetTagId} Not Found {roRec.Message}");
            if (roRec.Process != "TEST-PASS") return new CS_Result(false, $@"Item {assetTagId} Not in TEST-PASS Process");
            if (roRec.CurrentHubId != currHubId) return new CS_Result(false, $@"Item {assetTagId} Location {roRec.CurrentHubId} of Does NOT match Users Hub {currHubId}");

            roRec.UserName = userName;
            var csRec = GetCleanAndScreenRecord(roRec, true);  //See if rec exists 
            if ((!csRec.Success) && csRec.Message.Contains("ERROR")) return new CS_Result(false, csRec.Message);

            if (string.IsNullOrEmpty(csRec.MW_User)) //Rec exists, but mw BOM parts not made yet
            {
                RespModel rm = null;
                if (!csRec.Success) rm = CreateCsRecordAndParts(roRec, false);
                else rm = CreateCsRecordAndParts(roRec, true);

                if (!rm.Success) return new CS_Result(false, rm.Message);
            }

            return GetCleanAndScreenRecord(roRec, false); //Return Rec and BOM parts
        } //GetMakeWholeRefurbList


        private RespModel CreateCsRecordAndParts(ROHist roRec, bool mwRecAlreadyCreated)
        {
            var BomListObj = GetBomParts(roRec.MfgPart);

            using (var connection = dbHandler.CreateConnection())
            {
                using (var trans = connection.BeginTransaction())
                {
                    try
                    {
                        var param = new DynamicParameters();
                        param.Add(name: "AssetTagId", value: roRec.AssetTagId, direction: ParameterDirection.Input);
                        //param.Add(name: "CreatedBy", value: roRec.UserName, direction: ParameterDirection.Input);
                        param.Add(name: "MwUserId", value: roRec.UserName, direction: ParameterDirection.Input);
                        string mwQuery = string.Empty;
                        if (!mwRecAlreadyCreated)
                        {
                            mwQuery = @"
                                    INSERT INTO TMADMIN.CS_RESULTS
                                        (ASSET_TAG, MW_USER_ID, CREATED_BY) 
                                     VALUES 
                                        (:AssetTagId,  :MwUserId, :MwUserId) ";
                        }
                        else
                        {
                            mwQuery = @"
                                    UPDATE TMADMIN.CS_RESULTS 
                                        SET MW_USER_ID = :MwUserId, MODIFIED_BY = :MwUserId, MODIFIED_DATE = SYSDATE
                                    WHERE ASSET_TAG = :AssetTagId ";
                        }
                        var result = connection.Execute(mwQuery, param);

                        foreach (CS_Bom_Part bp in BomListObj.Bom_PartList)
                        {
                            var param2 = new DynamicParameters();
                            param2.Add(name: "AssetTagId", value: roRec.AssetTagId, direction: ParameterDirection.Input);
                            param2.Add(name: "PartNbr", value: bp.ComponentPartNbr, direction: ParameterDirection.Input);
                            param2.Add(name: "PartQty", value: bp.NeededQty, direction: ParameterDirection.Input);
                            param2.Add(name: "CreatedBy", value: roRec.UserName, direction: ParameterDirection.Input);

                            string insertQuery2 = @"INSERT INTO TMADMIN.CS_REFURB_PARTS
                                    (ASSET_TAG, MW_VERIFY_FLAG, COMPONENT_PART_NBR, COMPONENT_PART_QTY, CREATED_BY) 
                                     VALUES 
                                    (:AssetTagId,  'N',  :PartNbr, :PartQty, :CreatedBy) ";
                            var result2 = connection.Execute(insertQuery2, param2);
                        }

                        trans.Commit();
                        return new RespModel(true, "Created Asset BOM Records");
                    }
                    catch (Exception ex)
                    {
                        trans.Rollback();
                        return new RespModel(false, $@"DB ERROR in CreateCsRecordAndParts: {ex.Message}");
                    }

                } //trans
            } //using
        } //CreateCsRecordAndParts

        private CS_Bom_PartList GetBomParts(string mfgPartNbr)
        {

//#if DEBUG
//            mfgPartNbr = "474331A";
//#endif


            if (string.IsNullOrEmpty(mfgPartNbr)) mfgPartNbr = "";
            var tmpBomPart = mfgPartNbr.Replace("@", "").Replace("CL-", "").Trim();

            using (var connection = dbHandler.CreateConnection())
            {
                try
                {
                    var bomPartList = connection.Query<CS_Bom_Part>(@"
                                SELECT 
                                    BP.BOM_ID AS BomId,
                                    BP.COMPONENT_PART_NBR AS ComponentPartNbr,
                                    --COMPONENT_PART_DESC AS ComponentPartDesc,
                                    NVL(BP.NEEDED_QTY, 0) AS NeededQty,
                                    --IMAGE_URL AS ImageUrl,
                                    --BPI.IMG_TYPE AS ImageBytes,
                                    --BPI.IMG_DATA AS ImageType,
                                    --BPI.IMG_NAME AS ImageName,
                                    --BPI.PART_NOTE AS ComponentPartDesc,
                                    BP.AUTO_MW_FLAG AS Auto_MW_Flag
                                FROM TMADMIN.CS_BOM_PARTS BP
                                LEFT JOIN TMADMIN.CS_BOM_PART_IMG BPI ON BPI.PART_NBR = BP.COMPONENT_PART_NBR
                                WHERE BP.BOM_ID = :bomId 
                                ORDER BY BP.SORT_ID NULLS LAST, BP.NEEDED_QTY DESC, BP.COMPONENT_PART_NBR
                                ", new { bomId = tmpBomPart }).ToList();

                    var bList = new CS_Bom_PartList(true, "");
                    bList.Bom_PartList = bomPartList;

                    if (bList.Bom_PartList.Count() <= 0)
                    {
                        var bb = new CS_Bom_Part();
                        bb.BomId = "ZZZ";
                        bb.ComponentPartNbr = "Part Check";
                        bb.ComponentPartDesc = "Part Check Acknowledgement";
                        bb.NeededQty = 1;
                        bb.ImageUrl = null;
                        bb.Auto_MW_Flag = "N";
                    }

                    return bList; //Rule for empty list??
                }
                catch (Exception ex)
                {
                    return new CS_Bom_PartList(false, $@"DB ERROR in GetBomParts: {ex.Message}");
                }
            } //using
        } //GetBomParts


        public CS_Result GetCleanAndScreenRecord(ROHist roRec, bool csRecOnly = false)
        {
#if DEBUG
            //roRec.MfgPart = "473095A";
#endif

            var tmp = $@"{roRec.MfgPart} ";
            var tmpBomPart = tmp.Replace("@", "").Replace("CL-", "").Trim();


            using (var connection = dbHandler.CreateConnection())
            {
                try
                {
                    CS_Result csRec = connection.Query<CS_Result>(@"
                                SELECT 
                                    CS.ASSET_TAG AS AssetTag,
                                    CS.MW_USER_ID AS MW_User,
                                    CS.MW_COMPLETE_DATE AS MW_CompleteDate,
                                    CS.QC_USER_ID AS QC_User,
                                    CS.QC_STATUS AS QC_Status,
                                    CS.QC_GROUP_ID AS QC_GroupID,
                                    CS.QC_DATE AS QC_Date,
                                    CS.PTP_USER_ID AS PTP_User,
                                    CS.PTP_Date,
                                    CS.Cosmetic_Pass,
                                    CS.Cosmetic_Fail_Reason_Text,
                                    CS.Cleaning_Needed,
                                    CS.Cleaning_User_Id,
                                    CS.Created_Date
                                FROM TMADMIN.CS_RESULTS CS
                                WHERE CS.ASSET_TAG = :assetTag
                                ", new { assetTag = roRec.AssetTagId}).FirstOrDefault();

                    if (csRec == null) return new CS_Result(false, "NOT FOUND");
                    csRec.Success = true;
                    csRec.Message = "GOTCHA";
                    if (csRecOnly) return csRec;


                    CS_Result csRecImg = connection.Query<CS_Result>(@"
                                SELECT 
                                    BPI.IMG_TYPE AS ImageType,
                                    BPI.IMG_DATA AS ImageBytes,
                                    BPI.IMG_NAME AS ImageName,
                                    NVL(BPI.IMG_WIDTH, 400) AS ImageWidth,
                                    NVL(BPI.IMG_HEIGHT, 200) AS ImageHeight
                                    --NVL(BPI.PART_NOTE, ' ') AS ComponentPartDesc
                                FROM TMADMIN.CS_BOM_PART_IMG BPI 
                                WHERE BPI.PART_NBR = :mfgPart
                                ", new {mfgPart = tmpBomPart }).FirstOrDefault();

                    if ((csRecImg != null)  && (csRecImg.ImageBytes != null))
                    {
                        csRec.ImageData = $@"{Convert.ToBase64String(csRecImg.ImageBytes)}";
                        csRec.ImageType = csRecImg.ImageType;
                        csRec.ImageWidth = csRecImg.ImageWidth;
                        csRec.ImageHeight = csRecImg.ImageHeight;
                        csRec.ImageName = csRecImg.ImageName;
                        //csRec.ComponentPartDesc = csRecImg.ComponentPartDesc;
                    }


                    csRec.BOMList = connection.Query<CS_Refurb_Part>(@"
                                SELECT 
                                    CS.ASSET_TAG AS AssetTag,
                                    CS.MW_VERIFY_FLAG AS MW_VerifyFlag,
                                    CS.COMPONENT_PART_NBR ComponentPartNbr ,
                                    CS.COMPONENT_PART_QTY AS ComponentPartQty,
                                    BP.BIN,
                                    BPI.IMG_TYPE AS ImageType,
                                    BPI.IMG_DATA AS ImageBytes,
                                    BPI.IMG_NAME AS ImageName,
                                    NVL(BPI.IMG_WIDTH, 100) AS ImageWidth,
                                    NVL(BPI.IMG_HEIGHT, 100) AS ImageHeight,
                                    --NVL(BP.COMPONENT_PART_DESC, ' ') AS ComponentPartDesc,
                                    NVL(BPI.PART_NOTE, BP.COMPONENT_PART_DESC) AS ComponentPartDesc,
                                    ----BP.COMPONENT_PART_DESC AS ComponentPartDesc,
                                    ----BP.IMAGE_URL AS ImageUrl,
                                    BP.AUTO_MW_FLAG,
                                    NVL(CS.MODIFIED_BY,  CS.CREATED_BY) AS Modified_By,
                                    NVL(CS.MODIFIED_DATE,  CS.CREATED_DATE) AS Modified_Date
                                FROM TMADMIN.CS_REFURB_PARTS CS
                                INNER JOIN TMADMIN.CS_BOM_PARTS BP on BP.COMPONENT_PART_NBR = CS.COMPONENT_PART_NBR
                                    AND BP.BOM_ID = :mfgPart
                                LEFT JOIN TMADMIN.CS_BOM_PART_IMG BPI ON BPI.PART_NBR = BP.COMPONENT_PART_NBR

                                WHERE CS.ASSET_TAG = :assetTag
                                ORDER BY BP.SORT_ID NULLS LAST, CS.COMPONENT_PART_QTY DESC, CS.COMPONENT_PART_NBR 
                                ", new { mfgPart = tmpBomPart, assetTag = roRec.AssetTagId }).ToList();



                    
                    foreach(CS_Refurb_Part obj in csRec.BOMList)
                    {
                        if (obj.ImageBytes != null) obj.ImageData = $@"{Convert.ToBase64String(obj.ImageBytes)}";
                        //var tmpstr = $@"data:{obj.ImageType};base64, {Convert.ToBase64String(obj.ImageBytes)}";
                        //Due to base64 causeing 'unsafe' to be perperdended to string in frontend
                        //obj.ImageData = Regex.Replace(tmpstr, @"[\u000A\u000B\u000C\u000D\u2028\u2029\u0085]+", String.Empty);
                    }
                    

                    //if (csRec.BOMList.Count <= 0) return new CS_Result(false, $"NO BOM Parts Listed for Manufacturer Part {roRec.MfgPart}");

                    if (csRec.BOMList.Count <= 0) //Make a dummy as of 4/12/20121
                    {
                        var bb = new CS_Refurb_Part();
                        //bb.BomId = "ZZZ";
                        bb.AssetTag = roRec.AssetTagId;
                        bb.MW_VerifyFlag = "Y";
                        bb.ComponentPartNbr = "Part Check";
                        bb.ComponentPartDesc = "Part Check Acknowledgement";
                        bb.ComponentPartQty = 1;
                        bb.ImageUrl = null;
                        bb.Auto_MW_Flag = "Y";

                        csRec.BOMList.Add(bb);

                    }







                    return csRec;
                }
                catch(Exception ex)
                {
                    return new CS_Result(false, $@"DB ERROR in GetCleanAndScreenRecord: {ex.Message}");
                }
            } //using
        } //GetCleanAndScreenRecord(

        public static Bitmap ByteToImage(byte[] blob)
        {
            MemoryStream mStream = new MemoryStream();
            byte[] pData = blob;
            mStream.Write(pData, 0, Convert.ToInt32(pData.Length));
            Bitmap bm = new Bitmap(mStream, false);
            mStream.Dispose();
            return bm;
        } 


        public CS_Refurb_Part VerifyBomPartOnAsset(CS_Refurb_Part mwPart)
        {
            using (var connection = dbHandler.CreateConnection())
            {
                try
                {
                    var result = connection.Execute(@"
                        UPDATE TMADMIN.CS_REFURB_PARTS 
                            SET MW_VERIFY_FLAG = 'Y', MODIFIED_BY = :userName, MODIFIED_DATE = SYSDATE
                        WHERE ASSET_TAG = :assetTag AND COMPONENT_PART_NBR = :compPart 
                        ", new { assetTag = mwPart.AssetTag, userName = mwPart.UserName, compPart = mwPart.ComponentPartNbr });
                    if (result == 1)
                    {
                        mwPart.Success = true;
                        mwPart.Message = "";
                        return mwPart;
                    }

                    return new CS_Refurb_Part(false, $@"Verification Update of Part {mwPart.ComponentPartNbr} on Asset {mwPart.AssetTag} Failed");
                }
                catch (Exception ex)
                {
                    return new CS_Refurb_Part(false, $@"DB ERROR in VerifyBomPartOnAsset: {ex.Message}");
                }
            } //using

        }  //VerifyBomPartOnAsset


        public CS_Result UpdateCsRecordAsMwComplete(CS_Result csRec)
        {
            //Check that all parts are verified first
            var roRec = _RoHistService.GetRoHistRecordFromRoAssetTag(csRec.AssetTag);
            if (!roRec.Success) return new CS_Result(false, $@"Item {csRec.AssetTag} Not Found {roRec.Message}");
            if (roRec.Process != "TEST-PASS") return new CS_Result(false, $@"Asset {csRec.AssetTag} in not in Process TEST-PASS");

            using (var connection = dbHandler.CreateConnection())
            {
                CS_Result csCheck = GetCleanAndScreenRecord(roRec);

                if (csCheck.BOMList.Count() <= 0) return new CS_Result(false, "No Component Parts to Verify - Cannot Complete Make Whole Process");

                var UnVerifiedCount = csCheck.BOMList.Where(x => x.MW_VerifyFlag == "N").Count();
                if (UnVerifiedCount > 0) return new CS_Result(false, "Not all Component Parts Have Been Verified - Cannot Complete Make Whole Process");

                using (var trans = connection.BeginTransaction())
                {
                    try
                    {
                        var result1 = connection.Execute(@"
                        UPDATE TMADMIN.CS_RESULTS 
                            SET MW_USER_ID = :userName, MW_COMPLETE_DATE = SYSDATE, MODIFIED_BY = :userName, MODIFIED_DATE = SYSDATE
                        WHERE ASSET_TAG = :assetTag
                        ", new { assetTag = csRec.AssetTag, userName = csRec.UserName });

                        var result2 = connection.Execute(@"
                        UPDATE TMADMIN.RO_HIST 
                            SET PROCESS = 'REFURB', MODIFIED_BY = :userName, MODIFIED_DATE = SYSDATE
                        WHERE ASSET_TAG = :assetTag
                        ", new { assetTag = csRec.AssetTag, userName = csRec.UserName });


                        if (!((result1 == 1) && (result2 == 1)))
                        {
                            trans.Rollback();
                            csRec.Success = false;
                            csRec.Message = $@"{csRec.AssetTag} Failed to Update to the Make Whole Refurb Process {result1} {result2}";
                            return csRec;
                        }


                        InventoryTxnHistory invObj = new InventoryTxnHistory
                        {
                            HubId = roRec.CurrentHubId,
                            AssetTagId = roRec.AssetTagId,
                            PartNumber = "N/A",
                            TxnQuantity = 0,
                            UserId = csRec.UserName,
                            TxnDate = DateTime.Now,
                            TxnSource = "CleanAndScreen",
                            CreatedByApp = "CleanAndScreenMW",
                            TxnStatus = "X",
                            //FromContainer = obj.ContainerId,
                            //ToContainer = obj.ContainerId,
                            CreatedBy = csRec.UserName,
                            //FromPalletId = previousRecord.PalletId,
                            //ToPalletId = obj.PalletId,
                            //PalletDate = obj.PalletDate,
                            FromDisposition = roRec.Disposition,
                            ToDisposition = roRec.Disposition,
                            FromProcess = roRec.Process,
                            ToProcess = "REFURB",
                            //FromBin = previousRecord.PutAwayBin,
                            //ToBin = obj.Bin != null ? obj.Bin : previousRecord.PutAwayBin,
                            //FromInvStatus = previousRecord.InvStatus,
                            //ToInvStatus = previousRecord.InvStatus
                            //BusinessUnit = previousRecord.BusinessUnit,
                            //OrderNumber = previousRecord.OrderNumber
                        };
                        var invTxnNumber = _InventoryTxnHistoryService.Insert(invObj);

                        trans.Commit();
                        csRec.Success = true;
                        csRec.Message = $@"{csRec.AssetTag} has Finished the Make Whole Refurb Process";
                        return csRec;
                    }
                    catch (Exception ex)
                    {
                        trans.Rollback();
                        return new CS_Result(false, $@"DB ERROR in UpdateCsRecordAsMwComplete: {ex.Message}");
                    }
                }
            } //using
        } //UpdateCsRecordAsMwComplete


        //QC 


        public CS_Result GetQcRefurbList(string assetTagId, string userName, string currHubId)
        {
            var roRec = _RoHistService.GetRoHistRecordFromRoAssetTag(assetTagId);
            if (!roRec.Success) return new CS_Result(false, $@"Item {assetTagId} Not Found {roRec.Message}");
            if (! ((roRec.Process == "REFURB") || (roRec.Process == "QC-FAIL")) ) return new CS_Result(false, $@"Item {assetTagId} Not in REFURB nor QC-FAIL Process");
            if (roRec.CurrentHubId != currHubId) return new CS_Result(false, $@"Item {assetTagId} Location {roRec.CurrentHubId} of Does NOT match Users Hub {currHubId}");

            roRec.UserName = userName;
            return GetCleanAndScreenRecord(roRec);
        } //GetMakeWholeRefurbList


        //Save Full QC Results  with new GroupId
        public CS_Result UpdateCsRecordQcStatus(CS_Result csRec)
        {
            //Check that all parts are verified first
            var roRec = _RoHistService.GetRoHistRecordFromRoAssetTag(csRec.AssetTag);
            if (!roRec.Success) return new CS_Result(false, $@"Item {csRec.AssetTag} Not Found {roRec.Message}");
            if (!((roRec.Process == "REFURB") || (roRec.Process == "QC-FAIL"))) return new CS_Result(false, $@"Item {csRec.AssetTag} Not in REFURB nor QC-FAIL Process");

            var NextProcess = (csRec.QC_Status == "PASS") ? "QC-PASS" : "QC-FAIL";

            using (var connection = dbHandler.CreateConnection())
            {
                var QcGroupId = _SeqNbrService.GetSequenceNextVal("SEQ_QC_TEST_IDS");

                using (var trans = connection.BeginTransaction())
                {
                    try
                    {
                        var result1 = connection.Execute(@"
                        UPDATE TMADMIN.CS_RESULTS 
                            SET QC_STATUS = :qcStatus, QC_DATE = SYSDATE, QC_GROUP_ID = :qcGroup, QC_USER_ID = :userName, 
                            QC_FAIL_REASON_TEXT = :failReason, MODIFIED_BY = :userName, MODIFIED_DATE = SYSDATE
                        WHERE ASSET_TAG = :assetTag
                        ", new { qcStatus = csRec.QC_Status, qcGroup = QcGroupId, failReason = csRec.QC_Fail_Reason_Text, assetTag = csRec.AssetTag, userName = csRec.UserName });

                        var result2 = connection.Execute(@"
                        UPDATE TMADMIN.RO_HIST 
                            SET PROCESS = :qcStatus, MODIFIED_BY = :userName, MODIFIED_DATE = SYSDATE
                        WHERE ASSET_TAG = :assetTag
                        ", new { qcStatus = NextProcess, assetTag = csRec.AssetTag, userName = csRec.UserName });


                        var result3 = 1;
                        foreach (CS_Refurb_Part qcRec in csRec.BOMList)
                        {
                            var qcInsert = connection.Execute(@"
                                    INSERT INTO TMADMIN.CS_QC_PARTS
                                        (ASSET_TAG, GROUP_ID, COMPONENT_PART_NBR, COMPONENT_PART_QTY, PASS_FAIL_FLAG, FAIL_REASON, CREATED_BY)
                                    VALUES
                                        (:assetTag, :qcGroup, :compPart, :compQty, :compFlag, :compFailReason, :userName)
                        ", new
                            {
                                assetTag = csRec.AssetTag,
                                qcGroup = QcGroupId,
                                compPart = qcRec.ComponentPartNbr,
                                compQty = qcRec.ComponentPartQty,
                                compFlag = qcRec.PassFail_Flag,
                                compFailReason = qcRec.FailReason,
                                userName = csRec.UserName
                            });

                            if (qcInsert != 1) result3 = 0;
                        }


                        if (!((result1 == 1) && (result2 == 1) && (result3 == 1)))
                        {
                            trans.Rollback();
                            csRec.Success = false;
                            csRec.Message = $@"{csRec.AssetTag} Failed to Update its QC Process {result1} {result2} {result3}";
                            return csRec;
                        }


                        InventoryTxnHistory invObj = new InventoryTxnHistory
                        {
                            HubId = roRec.CurrentHubId,
                            AssetTagId = roRec.AssetTagId,
                            PartNumber = "N/A",
                            TxnQuantity = 0,
                            UserId = csRec.UserName,
                            TxnDate = DateTime.Now,
                            TxnSource = "CleanAndScreen",
                            CreatedByApp = "CleanAndScreenQC",
                            TxnStatus = "X",
                            //FromContainer = obj.ContainerId,
                            //ToContainer = obj.ContainerId,
                            CreatedBy = csRec.UserName,
                            //FromPalletId = previousRecord.PalletId,
                            //ToPalletId = obj.PalletId,
                            //PalletDate = obj.PalletDate,
                            FromDisposition = roRec.Disposition,
                            ToDisposition = roRec.Disposition,
                            FromProcess = roRec.Process,
                            ToProcess = NextProcess,
                            //FromBin = previousRecord.PutAwayBin,
                            //ToBin = obj.Bin != null ? obj.Bin : previousRecord.PutAwayBin,
                            //FromInvStatus = previousRecord.InvStatus,
                            //ToInvStatus = previousRecord.InvStatus
                            //BusinessUnit = previousRecord.BusinessUnit,
                            //OrderNumber = previousRecord.OrderNumber
                        };
                        var invTxnNumber = _InventoryTxnHistoryService.Insert(invObj);

                        trans.Commit();
                        csRec.Success = true;
                        csRec.Message = $@"The Process of {csRec.AssetTag} has been Updated to {NextProcess}";
                        return csRec;
                    }
                    catch (Exception ex)
                    {
                        trans.Rollback();
                        return new CS_Result(false, $@"DB ERROR in UpdateCsRecordQcStatus: {ex.Message}");
                    }
                }
            } //using
        } //UpdateCsRecordAsMwComplete



        //***********************************************************************************************************************************
        //***********************************************************************************************************************************

        //public IEnumerable<BomPart> GetAll()
        //{
        //    using (var connection = dbHandler.CreateConnection())
        //    {
        //        return connection.Query<BomPart>($"SELECT * FROM VNOADMIN.BOM_PART");
        //    }
        //}

        //public IEnumerable<BomComponent> GetBomPartsWithQty(string assetTagId)
        //{
        //    using (var connection = dbHandler.CreateConnection())
        //    {
        //        return connection.Query<BomComponent>(@"
        //            SELECT B.BIN_ID, ROWNUM, C.ASSET_TAG, 
        //            C.COMPONENT_PART_ID, C.MW_COMPLETE_DATE, B.NEEDED_QTY, 
        //                b.*  FROM vnoadmin.bom_part b
        //         LEFT JOIN vnoadmin.component_part c ON B.BOM_ID = C.BOM_ID 
        //            AND B.COMPONENT_PART_NBR = C.COMPONENT_PART_NBR 
        //            AND B.BIN_ID = C.BIN_ID 
        //            AND C.ASSET_TAG = :assetTagId AND NVL(C.DELETE_FLAG,'N') <> 'Y' ", new { assetTagId = assetTagId });
        //    }
        //}

        public IEnumerable<BomComponent> GetBomPartsQC(string assetTagId, string partNumber) //NOT USED
        {
            using (var connection = dbHandler.CreateConnection())
            {
                return connection.Query<BomComponent>(@"
                                                        SELECT B.BOM_ID, B.BIN_ID, B.COMPONENT_PART_NBR, B.COMPONENT_PART_DESC, B.NEEDED_QTY,
                                                        B.IMAGE_URL, C.ASSET_TAG, C.COMPONENT_PART_QTY, C.VERIFY_FLAG, 
                                                        C.CREATED_DATE COMPONENT_PART_CREATE_DATE, C.COMPONENT_PART_ID 
                                                        FROM VNOADMIN.BOM_PART B 
                                                        LEFT JOIN VNOADMIN.COMPONENT_PART C ON B.BOM_ID = C.BOM_ID 
                                                            AND B.component_part_nbr = C.COMPONENT_PART_NBR 
                                                            AND B.BIN_ID = C.BIN_ID 
                                                            AND C.ASSET_TAG = :assetTagId
                                                        WHERE B.BOM_ID = :partNumber", new { assetTagId = assetTagId, partNumber = partNumber });
            }
        }








    }
}
