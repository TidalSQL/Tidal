CREATE DATABASE OrdersDB;
GO

USE OrdersDB;
GO

SET NOCOUNT ON;

------------------------------------------------------------
-- Customers
------------------------------------------------------------
CREATE TABLE Customers
(
    CustomerId INT IDENTITY(1,1) PRIMARY KEY,
    FirstName NVARCHAR(50) NOT NULL,
    LastName NVARCHAR(50) NOT NULL,
    Email NVARCHAR(100) NOT NULL UNIQUE,
    Phone NVARCHAR(25) NULL,
    CreatedDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);

------------------------------------------------------------
-- Products
------------------------------------------------------------
CREATE TABLE Products
(
    ProductId INT IDENTITY(1,1) PRIMARY KEY,
    SKU NVARCHAR(30) NOT NULL UNIQUE,
    ProductName NVARCHAR(100) NOT NULL,
    UnitPrice DECIMAL(10,2) NOT NULL,
    StockQuantity INT NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1
);

------------------------------------------------------------
-- Invoices
------------------------------------------------------------
CREATE TABLE Invoices
(
    InvoiceId BIGINT IDENTITY(1,1) PRIMARY KEY,
    InvoiceNumber NVARCHAR(30) NOT NULL UNIQUE,
    CustomerId INT NOT NULL,
    InvoiceDate DATE NOT NULL,
    Status NVARCHAR(20) NOT NULL,
    Subtotal DECIMAL(12,2) NOT NULL DEFAULT 0,
    TaxAmount DECIMAL(12,2) NOT NULL DEFAULT 0,
    TotalAmount DECIMAL(12,2) NOT NULL DEFAULT 0,

    CONSTRAINT FK_Invoices_Customers
        FOREIGN KEY (CustomerId)
        REFERENCES Customers(CustomerId)
);

------------------------------------------------------------
-- Invoice line items
--
-- One invoice can contain many products.
------------------------------------------------------------
CREATE TABLE InvoiceItems
(
    InvoiceItemId BIGINT IDENTITY(1,1) PRIMARY KEY,
    InvoiceId BIGINT NOT NULL,
    ProductId INT NOT NULL,
    Quantity INT NOT NULL,
    UnitPrice DECIMAL(10,2) NOT NULL,

    LineTotal AS
        (CONVERT(DECIMAL(12,2), Quantity * UnitPrice)) PERSISTED,

    CONSTRAINT FK_InvoiceItems_Invoices
        FOREIGN KEY (InvoiceId)
        REFERENCES Invoices(InvoiceId),

    CONSTRAINT FK_InvoiceItems_Products
        FOREIGN KEY (ProductId)
        REFERENCES Products(ProductId)
);

------------------------------------------------------------
-- Useful indexes
------------------------------------------------------------
CREATE INDEX IX_Invoices_CustomerId
    ON Invoices(CustomerId);

CREATE INDEX IX_Invoices_InvoiceDate
    ON Invoices(InvoiceDate);

CREATE INDEX IX_InvoiceItems_InvoiceId
    ON InvoiceItems(InvoiceId);

CREATE INDEX IX_InvoiceItems_ProductId
    ON InvoiceItems(ProductId);

------------------------------------------------------------
-- Generate 50 Customers
------------------------------------------------------------
;WITH Numbers AS
(
    SELECT TOP (50)
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
    FROM sys.all_objects
)
INSERT INTO Customers
(
    FirstName,
    LastName,
    Email,
    Phone
)
SELECT
    CONCAT('Customer', N),
    CONCAT('LastName', N),
    CONCAT('customer', N, '@example.com'),
    CONCAT('555-100-', RIGHT('0000' + CAST(N AS VARCHAR(4)), 4))
FROM Numbers;

------------------------------------------------------------
-- Generate 1,000 Products
------------------------------------------------------------
;WITH Numbers AS
(
    SELECT TOP (1000)
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
    FROM sys.all_objects
)
INSERT INTO Products
(
    SKU,
    ProductName,
    UnitPrice,
    StockQuantity
)
SELECT
    CONCAT('SKU-', RIGHT('000000' + CAST(N AS VARCHAR(6)), 6)),
    CONCAT('Product ', N),

    CAST(
        5.00 + ((N % 500) * 0.75)
        AS DECIMAL(10,2)
    ),

    100 + (N % 900)
FROM Numbers;

------------------------------------------------------------
-- Generate 10,000 Invoices
--
-- Customer IDs rotate between 1 and 50.
------------------------------------------------------------
;WITH Numbers AS
(
    SELECT TOP (10000)
        ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS N
    FROM sys.all_objects A
    CROSS JOIN sys.all_objects B
)
INSERT INTO Invoices
(
    InvoiceNumber,
    CustomerId,
    InvoiceDate,
    Status
)
SELECT
    CONCAT(
        'INV-',
        RIGHT('00000000' + CAST(N AS VARCHAR(8)), 8)
    ),

    ((N - 1) % 50) + 1,

    DATEADD(
        DAY,
        -((N - 1) % 730),
        CAST(GETDATE() AS DATE)
    ),

    CASE
        WHEN N % 20 = 0 THEN 'Cancelled'
        WHEN N % 5 = 0 THEN 'Pending'
        ELSE 'Paid'
    END
FROM Numbers;

------------------------------------------------------------
-- Generate 3 line items per invoice
--
-- 10,000 invoices * 3 items = 30,000 InvoiceItems
------------------------------------------------------------
INSERT INTO InvoiceItems
(
    InvoiceId,
    ProductId,
    Quantity,
    UnitPrice
)
SELECT
    I.InvoiceId,
    P.ProductId,
    ((I.InvoiceId + V.LineNumber) % 5) + 1,
    P.UnitPrice
FROM Invoices I
CROSS JOIN
(
    VALUES (1), (2), (3)
) V(LineNumber)
JOIN Products P
    ON P.ProductId =
       ((I.InvoiceId * 17 + V.LineNumber * 37) % 1000) + 1;

------------------------------------------------------------
-- Calculate Invoice totals
------------------------------------------------------------
;WITH InvoiceTotals AS
(
    SELECT
        InvoiceId,
        SUM(CONVERT(DECIMAL(12,2), Quantity * UnitPrice)) AS Subtotal
    FROM InvoiceItems
    GROUP BY InvoiceId
)
UPDATE I
SET
    I.Subtotal = T.Subtotal,
    I.TaxAmount = ROUND(T.Subtotal * 0.10, 2),
    I.TotalAmount =
        T.Subtotal + ROUND(T.Subtotal * 0.10, 2)
FROM Invoices I
JOIN InvoiceTotals T
    ON I.InvoiceId = T.InvoiceId;

------------------------------------------------------------
-- Verify row counts
------------------------------------------------------------
SELECT 'Customers' AS Entity, COUNT(*) AS [RowCount]
FROM Customers

UNION ALL

SELECT 'Products', COUNT(*)
FROM Products

UNION ALL

SELECT 'Invoices', COUNT(*)
FROM Invoices

UNION ALL

SELECT 'InvoiceItems', COUNT(*)
FROM InvoiceItems;

------------------------------------------------------------
-- Example query
------------------------------------------------------------
SELECT TOP 100
    I.InvoiceNumber,
    I.InvoiceDate,
    C.CustomerId,
    C.FirstName,
    C.LastName,
    P.ProductName,
    II.Quantity,
    II.UnitPrice,
    II.LineTotal,
    I.TotalAmount
FROM Invoices I
JOIN Customers C
    ON C.CustomerId = I.CustomerId
JOIN InvoiceItems II
    ON II.InvoiceId = I.InvoiceId
JOIN Products P
    ON P.ProductId = II.ProductId
ORDER BY I.InvoiceId;