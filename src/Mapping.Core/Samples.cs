namespace Mapping.Core;

/// <summary>Made-up example data for the demo and the tests. No real company, address or person.</summary>
public static class Samples
{
    public const string PartnerXml = """
        <PurchaseOrder>
          <Header>
            <PONumber>PO-10042</PONumber>
            <OrderDate>03/14/2026</OrderDate>
            <BuyerName>Acme Industrial Supply</BuyerName>
            <BuyerEmail>purchasing@acme.example</BuyerEmail>
            <Currency>USD</Currency>
          </Header>
          <ShipTo>
            <Street>400 Harbor Way</Street>
            <City>Baltimore</City>
            <PostalCode>21201</PostalCode>
          </ShipTo>
          <Lines>
            <Line>
              <Sku>HX-220</Sku>
              <Qty>12</Qty>
              <UnitPrice>$14.50</UnitPrice>
            </Line>
            <Line>
              <Sku>HX-310</Sku>
              <Qty>4</Qty>
              <UnitPrice>$89.00</UnitPrice>
            </Line>
          </Lines>
        </PurchaseOrder>
        """;

    public const string EdiOrder = """
        BEG*00*SA*PO-77310**20260321~
        N1*BY*Northwind Traders~
        N3*12 Quay Street~
        N4*Seattle*WA*98101~
        PO1*1*8*EA*22.75**VP*GX-100~
        PO1*2*3*EA*105.00**VP*GX-200~
        """;

    public const string InternalOrderSchema = """
        # path | type | description
        /Order/Id | string | Order identifier (purchase order number)
        /Order/Date | date | Order date
        /Order/Customer/Name | string | Customer name
        /Order/Customer/Email | string | Customer email address
        /Order/Currency | string | Currency code
        /Order/ShipTo/Address | string | Street address
        /Order/ShipTo/City | string | City
        /Order/ShipTo/Zip | string | Postal code
        /Order/Item/ProductCode | string | Product code (SKU)
        /Order/Item/Quantity | number | Quantity ordered
        /Order/Item/Price | number | Unit price
        /Order/Notes | string | Free-text notes
        """;

    public const string EdiSchema = """
        # path | type | description
        BEG.03 | string | Purchase order number
        BEG.05 | date | Order date
        N1.02 | string | Buyer name
        N3.01 | string | Street address
        N4.01 | string | City
        N4.03 | string | Postal code
        PO1.02 | number | Quantity
        PO1.04 | number | Unit price
        PO1.07 | string | Product code
        """;
}
