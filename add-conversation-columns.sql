-- Add missing columns to conversations table for topic tracking
ALTER TABLE conversations
ADD COLUMN IF NOT EXISTS "ActiveDocumentIds" text,
ADD COLUMN IF NOT EXISTS "TopicSignature" text,
ADD COLUMN IF NOT EXISTS "LastTopicQuery" text;
