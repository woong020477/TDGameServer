-- ProjectMine: one-time initialization of the empty projectmine database.
-- Import with a database administrator, not the runtime projectmine_app account.
-- Do not use mysql --force. If any statement fails, inspect before retrying.
-- MySQL DDL is not transactional across this file; partial creation is possible.
-- Table casing matches GameServerApplication.cs on case-sensitive Linux hosts.
-- Existing tables are never dropped, and no test users or credentials are seeded.

USE `projectmine`;
SET NAMES utf8mb4;

CREATE TABLE `Titles` (
  `TitleId` int NOT NULL AUTO_INCREMENT,
  `TitleName` varchar(50) NOT NULL,
  `ColorGradient` varchar(255) NOT NULL,
  PRIMARY KEY (`TitleId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `Users` (
  `Id` int NOT NULL AUTO_INCREMENT,
  `Username` varchar(50) NOT NULL,
  `Email` varchar(100) NOT NULL,
  `PasswordHash` varchar(255) NOT NULL,
  `CreatedAt` datetime DEFAULT CURRENT_TIMESTAMP,
  `TitleId` int DEFAULT '0',
  PRIMARY KEY (`Id`),
  UNIQUE KEY `unique_username` (`Username`),
  UNIQUE KEY `unique_email` (`Email`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `Rooms` (
  `RoomId` int NOT NULL AUTO_INCREMENT,
  `RoomName` varchar(50) NOT NULL,
  `HostId` int NOT NULL,
  `Host` varchar(50) NOT NULL,
  `Difficulty` enum('Easy','Normal','Hard','Challenge') NOT NULL,
  `CurrentPlayers` int DEFAULT '1',
  `MaxPlayers` int DEFAULT '4',
  `CreatedAt` timestamp NULL DEFAULT CURRENT_TIMESTAMP,
  PRIMARY KEY (`RoomId`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

CREATE TABLE `RoomPlayers` (
  `RoomPlayerId` int NOT NULL AUTO_INCREMENT,
  `RoomId` int NOT NULL,
  `UserId` int NOT NULL,
  `Username` varchar(50) NOT NULL,
  `PlayerSlot` int NOT NULL,
  `IsHost` tinyint(1) DEFAULT '0',
  `IsClosed` tinyint(1) DEFAULT '0',
  PRIMARY KEY (`RoomPlayerId`),
  UNIQUE KEY `RoomId` (`RoomId`,`PlayerSlot`),
  CONSTRAINT `roomplayers_ibfk_1` FOREIGN KEY (`RoomId`) REFERENCES `Rooms` (`RoomId`) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_0900_ai_ci;

-- Preserve the existing title IDs and client gradient keys.
INSERT INTO `Titles` (`TitleId`, `TitleName`, `ColorGradient`) VALUES
  (1, 'GM', 'Gradient1'),
  (2, 'Bronze', 'Gradient2'),
  (3, 'Silver', 'Gradient3'),
  (4, 'Gold', 'Gradient4'),
  (5, 'Platinum', 'Gradient5'),
  (6, 'Diamond', 'Gradient6'),
  (7, 'Challenger', 'Gradient7'),
  (8, 'Disney', 'Gradient8'),
  (9, 'DU', 'Gradient9');
