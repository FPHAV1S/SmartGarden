--
-- PostgreSQL database dump
--

\restrict oeCb3tiJMSmtcKdWYaud1ix6NfcI9B76VgNkRI4kaZmDbjebhUKQ2tb1vzRZ3Rb

-- Dumped from database version 17.8 (Debian 17.8-0+deb13u1)
-- Dumped by pg_dump version 17.8 (Debian 17.8-0+deb13u1)

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET transaction_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

SET default_tablespace = '';

SET default_table_access_method = heap;

--
-- Name: irrigation_events; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.irrigation_events (
    id integer NOT NULL,
    zone_id integer,
    started_at timestamp without time zone DEFAULT now(),
    ended_at timestamp without time zone,
    duration_sec integer,
    trigger_reason character varying(100),
    moisture_before double precision,
    moisture_after double precision
);


ALTER TABLE public.irrigation_events OWNER TO denis;

--
-- Name: irrigation_events_id_seq; Type: SEQUENCE; Schema: public; Owner: denis
--

CREATE SEQUENCE public.irrigation_events_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.irrigation_events_id_seq OWNER TO denis;

--
-- Name: irrigation_events_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: denis
--

ALTER SEQUENCE public.irrigation_events_id_seq OWNED BY public.irrigation_events.id;


--
-- Name: login_attempts; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.login_attempts (
    id integer NOT NULL,
    ip_address character varying(50) NOT NULL,
    username character varying(50),
    success boolean NOT NULL,
    attempted_at timestamp without time zone DEFAULT now()
);


ALTER TABLE public.login_attempts OWNER TO denis;

--
-- Name: login_attempts_id_seq; Type: SEQUENCE; Schema: public; Owner: denis
--

CREATE SEQUENCE public.login_attempts_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.login_attempts_id_seq OWNER TO denis;

--
-- Name: login_attempts_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: denis
--

ALTER SEQUENCE public.login_attempts_id_seq OWNED BY public.login_attempts.id;


--
-- Name: sensor_readings; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.sensor_readings (
    id integer NOT NULL,
    zone_id integer,
    moisture double precision,
    temperature double precision,
    humidity double precision,
    recorded_at timestamp without time zone DEFAULT now()
);


ALTER TABLE public.sensor_readings OWNER TO denis;

--
-- Name: sensor_readings_id_seq; Type: SEQUENCE; Schema: public; Owner: denis
--

CREATE SEQUENCE public.sensor_readings_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.sensor_readings_id_seq OWNER TO denis;

--
-- Name: sensor_readings_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: denis
--

ALTER SEQUENCE public.sensor_readings_id_seq OWNED BY public.sensor_readings.id;


--
-- Name: system_logs; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.system_logs (
    id integer NOT NULL,
    level character varying(20),
    message text,
    logged_at timestamp without time zone DEFAULT now()
);


ALTER TABLE public.system_logs OWNER TO denis;

--
-- Name: system_logs_id_seq; Type: SEQUENCE; Schema: public; Owner: denis
--

CREATE SEQUENCE public.system_logs_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.system_logs_id_seq OWNER TO denis;

--
-- Name: system_logs_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: denis
--

ALTER SEQUENCE public.system_logs_id_seq OWNED BY public.system_logs.id;


--
-- Name: system_settings; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.system_settings (
    id integer NOT NULL,
    auto_watering_enabled boolean DEFAULT true,
    system_mode character varying(20) DEFAULT 'auto'::character varying,
    default_watering_duration integer DEFAULT 10,
    night_mode_enabled boolean DEFAULT false,
    night_mode_start_hour integer DEFAULT 18,
    night_mode_end_hour integer DEFAULT 8,
    eco_mode_enabled boolean DEFAULT false,
    updated_at timestamp without time zone DEFAULT now()
);


ALTER TABLE public.system_settings OWNER TO denis;

--
-- Name: system_settings_id_seq; Type: SEQUENCE; Schema: public; Owner: denis
--

CREATE SEQUENCE public.system_settings_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.system_settings_id_seq OWNER TO denis;

--
-- Name: system_settings_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: denis
--

ALTER SEQUENCE public.system_settings_id_seq OWNED BY public.system_settings.id;


--
-- Name: users; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.users (
    id integer NOT NULL,
    username character varying(50) NOT NULL,
    password_hash character varying(255) NOT NULL,
    created_at timestamp without time zone DEFAULT now()
);


ALTER TABLE public.users OWNER TO denis;

--
-- Name: users_id_seq; Type: SEQUENCE; Schema: public; Owner: denis
--

CREATE SEQUENCE public.users_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.users_id_seq OWNER TO denis;

--
-- Name: users_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: denis
--

ALTER SEQUENCE public.users_id_seq OWNED BY public.users.id;


--
-- Name: zones; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.zones (
    id integer NOT NULL,
    name character varying(50) NOT NULL,
    plant_type character varying(50),
    moisture_threshold double precision DEFAULT 30.0 NOT NULL,
    is_active boolean DEFAULT true,
    created_at timestamp without time zone DEFAULT now()
);


ALTER TABLE public.zones OWNER TO denis;

--
-- Name: ai_irrigation_decision_logs; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.ai_irrigation_decision_logs (
    id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    "timestamp" timestamp without time zone DEFAULT now() NOT NULL,
    sensor_reading_id integer REFERENCES public.sensor_readings(id) ON DELETE SET NULL,
    zone_id integer NOT NULL REFERENCES public.zones(id) ON DELETE CASCADE,
    moisture_percent double precision,
    ai_was_attempted boolean DEFAULT false NOT NULL,
    ai_should_water boolean DEFAULT false NOT NULL,
    final_should_water_after_safety boolean DEFAULT false NOT NULL,
    recommended_valve_state character varying(3) DEFAULT 'OFF'::character varying NOT NULL,
    final_valve_state character varying(3) DEFAULT 'OFF'::character varying NOT NULL,
    recommended_duration_seconds integer DEFAULT 0 NOT NULL,
    confidence double precision DEFAULT 0 NOT NULL,
    reason text,
    learned_observation text,
    suggested_moisture_threshold double precision,
    risk_level character varying(20) DEFAULT 'LOW'::character varying NOT NULL,
    was_fallback_used boolean DEFAULT true NOT NULL,
    error_message text,
    safety_notes text
);


ALTER TABLE public.ai_irrigation_decision_logs OWNER TO denis;

--
-- Name: pattern_memory; Type: TABLE; Schema: public; Owner: denis
--

CREATE TABLE public.pattern_memory (
    id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    created_at timestamp without time zone DEFAULT now() NOT NULL,
    summary text NOT NULL,
    suggested_threshold double precision,
    average_moisture double precision,
    notes text
);


ALTER TABLE public.pattern_memory OWNER TO denis;

--
-- Name: zones_id_seq; Type: SEQUENCE; Schema: public; Owner: denis
--

CREATE SEQUENCE public.zones_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER SEQUENCE public.zones_id_seq OWNER TO denis;

--
-- Name: zones_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: denis
--

ALTER SEQUENCE public.zones_id_seq OWNED BY public.zones.id;


--
-- Name: irrigation_events id; Type: DEFAULT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.irrigation_events ALTER COLUMN id SET DEFAULT nextval('public.irrigation_events_id_seq'::regclass);


--
-- Name: login_attempts id; Type: DEFAULT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.login_attempts ALTER COLUMN id SET DEFAULT nextval('public.login_attempts_id_seq'::regclass);


--
-- Name: sensor_readings id; Type: DEFAULT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.sensor_readings ALTER COLUMN id SET DEFAULT nextval('public.sensor_readings_id_seq'::regclass);


--
-- Name: system_logs id; Type: DEFAULT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.system_logs ALTER COLUMN id SET DEFAULT nextval('public.system_logs_id_seq'::regclass);


--
-- Name: system_settings id; Type: DEFAULT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.system_settings ALTER COLUMN id SET DEFAULT nextval('public.system_settings_id_seq'::regclass);


--
-- Name: users id; Type: DEFAULT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.users ALTER COLUMN id SET DEFAULT nextval('public.users_id_seq'::regclass);


--
-- Name: zones id; Type: DEFAULT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.zones ALTER COLUMN id SET DEFAULT nextval('public.zones_id_seq'::regclass);


--
-- Name: irrigation_events irrigation_events_pkey; Type: CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.irrigation_events
    ADD CONSTRAINT irrigation_events_pkey PRIMARY KEY (id);


--
-- Name: login_attempts login_attempts_pkey; Type: CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.login_attempts
    ADD CONSTRAINT login_attempts_pkey PRIMARY KEY (id);


--
-- Name: sensor_readings sensor_readings_pkey; Type: CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.sensor_readings
    ADD CONSTRAINT sensor_readings_pkey PRIMARY KEY (id);


--
-- Name: system_logs system_logs_pkey; Type: CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.system_logs
    ADD CONSTRAINT system_logs_pkey PRIMARY KEY (id);


--
-- Name: system_settings system_settings_pkey; Type: CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.system_settings
    ADD CONSTRAINT system_settings_pkey PRIMARY KEY (id);


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_pkey PRIMARY KEY (id);


--
-- Name: users users_username_key; Type: CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.users
    ADD CONSTRAINT users_username_key UNIQUE (username);


--
-- Name: zones zones_pkey; Type: CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.zones
    ADD CONSTRAINT zones_pkey PRIMARY KEY (id);


--
-- Name: idx_login_attempts_ip; Type: INDEX; Schema: public; Owner: denis
--

CREATE INDEX idx_login_attempts_ip ON public.login_attempts USING btree (ip_address, attempted_at);

--
-- Name: idx_ai_irrigation_decision_logs_timestamp; Type: INDEX; Schema: public; Owner: denis
--

CREATE INDEX idx_ai_irrigation_decision_logs_timestamp ON public.ai_irrigation_decision_logs USING btree ("timestamp" DESC);


--
-- Name: idx_ai_irrigation_decision_logs_zone_timestamp; Type: INDEX; Schema: public; Owner: denis
--

CREATE INDEX idx_ai_irrigation_decision_logs_zone_timestamp ON public.ai_irrigation_decision_logs USING btree (zone_id, "timestamp" DESC);


--
-- Name: idx_pattern_memory_created_at; Type: INDEX; Schema: public; Owner: denis
--

CREATE INDEX idx_pattern_memory_created_at ON public.pattern_memory USING btree (created_at DESC);


--
-- Name: irrigation_events irrigation_events_zone_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.irrigation_events
    ADD CONSTRAINT irrigation_events_zone_id_fkey FOREIGN KEY (zone_id) REFERENCES public.zones(id);


--
-- Name: sensor_readings sensor_readings_zone_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: denis
--

ALTER TABLE ONLY public.sensor_readings
    ADD CONSTRAINT sensor_readings_zone_id_fkey FOREIGN KEY (zone_id) REFERENCES public.zones(id);


--
-- Name: SCHEMA public; Type: ACL; Schema: -; Owner: pg_database_owner
--

GRANT ALL ON SCHEMA public TO denis;


--
-- PostgreSQL database dump complete
--

\unrestrict oeCb3tiJMSmtcKdWYaud1ix6NfcI9B76VgNkRI4kaZmDbjebhUKQ2tb1vzRZ3Rb

